using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Unreal;

/// <summary>An entry of the key list: the game as the list names it, and its pak key (64 hex digits).</summary>
public sealed record KeyEntry(string Name, string Key);

public enum KeyLookupOutcome { Unlocked, NoWorkingKey, FetchFailed, AlreadyTried }

/// <summary>A lookup's result, never with the key: <see cref="Entry"/> is the name of the list entry whose key opened the
/// game's files; <see cref="Tried"/> how many listed keys were checked.</summary>
public sealed record KeyLookup(KeyLookupOutcome Outcome, string Message, string? Entry = null, int Tried = 0);

public enum KeyImportOutcome { Unlocked, NoWorkingKey, Skipped }

/// <summary>A game's result of a key file import (<see cref="KeyCollection.ImportFile"/>), never with the key: <see cref="Entry"/>
/// names the file's entry that opened the game's files ("unnamed key #3" for a line without a name).</summary>
public sealed record KeyImportGame(string GameId, string Name, KeyImportOutcome Outcome, string Message, string? Entry = null, int Tried = 0);

/// <summary>A key file import: the named and unnamed keys read from the file, each Unreal game's result; Problem: why the
/// file gave nothing (then no game was tried); NotKept: why its keys weren't kept for later lookups (the games were tried).</summary>
public sealed record KeyImport(int Named, int Unnamed, IReadOnlyList<KeyImportGame> Games, string? Problem = null, string? NotKept = null)
{
    public int Unlocked => Games.Count(g => g.Outcome == KeyImportOutcome.Unlocked);
}

/// <summary>The community's list of Unreal pak keys: the first post of a forum topic (Settings.KeyListUrl), fetched over
/// HTTPS on the user's request or, with Settings.LookUpKeysOnline, after a scan finds an encrypted game without a key. A
/// plain GET of the page: nothing about the user or their games is sent; names are matched here. The page is untrusted:
/// only "name 0x&lt;64 hex&gt;" lines are taken from it, and each key is used only if it opens the game's files
/// (<see cref="UnrealKeys.Set"/>). The parsed list is kept in keys\collection.json with when it was fetched: refetched at
/// most every <see cref="AutoEvery"/>, on the user's request every <see cref="UserEvery"/>; a failure backs off and the
/// cached copy is used. Keys the user imports from a file (<see cref="ImportFile"/>) are kept apart, in keys\imported.json,
/// so a refetch never drops them; a lookup tries them first, without the network. That file and a game's aes.lookup are
/// replaced whole under a lock the app and the command line share (<see cref="AppStore.PathGate"/>).</summary>
public sealed class KeyCollection
{
    public const string DefaultUrl = "https://cs.rin.ru/forum/viewtopic.php?f=10&t=100672";
    public const int MaxBytes = 4 << 20, MaxEntries = 20_000, MaxCandidates = 20, MaxName = 120, MaxImportBytes = 16 << 20, MaxUnnamed = 50,
        MaxImportedUnnamed = 500;
    public static readonly TimeSpan AutoEvery = TimeSpan.FromHours(24), UserEvery = TimeSpan.FromHours(1), UserRetry = TimeSpan.FromMinutes(5),
        Timeout = TimeSpan.FromSeconds(20);
    public const string SecurityCheck = "the forum answered with a browser security check, which SCSKiller doesn't take: click Open list in browser, save the page (Ctrl+S), then Load saved page… (on the command line: --page <file>)";

    sealed record CacheFile(string Url, DateTimeOffset? FetchedAt, DateTimeOffset? TriedAt, int Failures, KeyEntry[] Entries);
    sealed record ImportedFile(KeyEntry[] Entries, string[]? Unnamed = null);   // Unnamed: none in a file from before they were kept
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly string file, imported;
    readonly HttpClient http;
    readonly TimeProvider clock;
    readonly SemaphoreSlim gate = new(1, 1);   // a scan's pass and the user's button: one fetch, not two

    public KeyCollection(string dataDir, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        file = Path.Combine(dataDir, "keys", "collection.json");
        imported = Path.Combine(dataDir, "keys", "imported.json");
        http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, MaxAutomaticRedirections = 5 }, handler == null)
            { Timeout = System.Threading.Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaxBytes };
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>When the cached list was fetched (or loaded from a saved page); null = none.</summary>
    public DateTimeOffset? FetchedAt => Load()?.FetchedAt;

    /// <summary>Looks the game up under <paramref name="names"/>: first in the user's imported keys (no network;
    /// <see cref="ImportedCandidates"/>), then, if none of them works and <paramref name="online"/>, in the list; each
    /// candidate key (<see cref="Candidates"/>), in order, goes to <paramref name="trySet"/>, which stores it only if it opens
    /// the game's files; the first it takes wins. A key is tried once per pass: the list's candidates leave out the imported ones.
    /// <paramref name="tried"/>: a file that remembers the candidates last tried in vain for the game with a stamp of its
    /// files, the list's on its first line and the imported ones on its second; a lookup that isn't <paramref name="userRequested"/>
    /// skips the same candidates for the same stamp (AlreadyTried when nothing was left to try), so a new import retries.
    /// A key found drops the file: once it is lost (deleted, or it no longer opens the files) the candidates are tried again.
    /// <paramref name="savedPage"/>: the list as the user saved it from a browser, used instead of fetching and cached.
    /// <paramref name="stage"/>: what it does now, for the user. The log names entries, never a key.</summary>
    public async Task<KeyLookup> LookUpAsync(string url, IEnumerable<string?> names, Func<string, bool> trySet, bool userRequested,
        (string File, string Stamp)? tried = null, IProgress<string>? log = null, CancellationToken ct = default, string? savedPage = null, bool online = true,
        IProgress<string>? stage = null)
    {
        var known = names.ToList();
        var had = ReadImported() ?? ([], []);
        var (mine, byName) = ImportedCandidates(had.Named, had.Unnamed, known);
        var memos = tried is { } t ? Remembered(t.File) : [];
        var mineMemo = tried is { } t1 ? Memo(t1.Stamp, mine) : null;
        var local = 0;   // imported keys tried in this pass
        string Mine(int i) => i < byName ? $"the key imported for \"{mine[i].Name}\"" : $"imported {mine[i].Name}";
        if (mine.Count > 0 && (userRequested || mineMemo == null || memos.ElementAtOrDefault(1) != mineMemo))
        {
            stage?.Report("Trying your imported keys…");
            for (; local < mine.Count; local++)
            {
                ct.ThrowIfCancellationRequested();
                log?.Report($"checking {Mine(local)}");
                if (!trySet(mine[local].Key)) continue;
                if (tried is { } found) Forget(found.File);
                return new(KeyLookupOutcome.Unlocked, $"Unlocked with {Mine(local)}, from your imported keys.", mine[local].Name, local + 1);
            }
            Remember(tried, 1, mineMemo);
        }
        var mineNote = local == 0 ? "" : local == 1 ? $"Your {(byName > 0 ? $"imported key for \"{mine[0].Name}\"" : Mine(0))} doesn't open this game's files. "
            : $"None of your {local} imported keys for this game opens its files. ";
        if (!online)
            return local > 0 ? new(KeyLookupOutcome.NoWorkingKey, mineNote.TrimEnd(), Tried: local)
                : new(KeyLookupOutcome.AlreadyTried, mine.Count == 0 ? "No imported key for this game." : "The imported keys for this game were already tried.");
        stage?.Report(savedPage != null ? "Checking the saved page…" : "Checking the community's key list…");
        var (list, problem) = savedPage != null ? Import(url, savedPage) : await ListAsync(url, userRequested, ct);
        if (list == null) return new(KeyLookupOutcome.FetchFailed, $"{mineNote}Couldn't get the key list: {problem}.", Tried: local);
        var stale = problem != null ? $" (the list couldn't be refreshed: {problem}; used the saved copy{(FetchedAt is { } at ? $" from {at.ToLocalTime():d MMM yyyy}" : "")})" : "";
        var imported = mine.Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = Candidates(list.Where(e => !imported.Contains(e.Key)), known);
        if (candidates.Count == 0) return new(KeyLookupOutcome.NoWorkingKey, $"{mineNote}The key list ({list.Count:N0} entries) has no entry for this game{stale}.", Tried: local);
        var memo = tried is { } t2 ? Memo(t2.Stamp, candidates) : null;
        if (!userRequested && memo != null && memos.ElementAtOrDefault(0) == memo)
            return local > 0 ? new(KeyLookupOutcome.NoWorkingKey, $"{mineNote}The keys listed for it were already tried.", Tried: local)
                : new(KeyLookupOutcome.AlreadyTried, "The keys listed for this game were already tried.");
        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            log?.Report($"checking the key listed for \"{candidates[i].Name}\"");
            if (!trySet(candidates[i].Key)) continue;
            if (tried is { } found) Forget(found.File);
            return new(KeyLookupOutcome.Unlocked, $"Unlocked with the key listed for \"{candidates[i].Name}\", from the online list{stale}.", candidates[i].Name, local + i + 1);
        }
        Remember(tried, 0, memo);
        return new(KeyLookupOutcome.NoWorkingKey, mineNote + (candidates.Count == 1
            ? $"The key listed for \"{candidates[0].Name}\" doesn't open this game's files{stale}."
            : $"None of the {candidates.Count} keys listed for this game ({string.Join(", ", candidates.Select(c => c.Name).Distinct().Take(4))}) opens its files{stale}."), Tried: local + candidates.Count);
    }

    static string Memo(string stamp, List<KeyEntry> candidates) =>
        $"{stamp}|{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', candidates.Select(c => c.Key.ToUpperInvariant())))))}";

    static string[] Remembered(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Split('\n') : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    // line 0: the list's candidates (the file's only line before imports existed), line 1: the imported ones, line 2: Failed,
    // which a file from before only failures were remembered lacks (ForgetLegacy)
    const string Failed = "failed";

    static void Remember((string File, string Stamp)? tried, int line, string? memo)
    {
        if (tried is not { } t || memo == null) return;
        try
        {
            using (new AppStore.PathGate(t.File))   // a scan's lookup and an import, in the app or the command line
            {
                var lines = Remembered(t.File).Concat(["", ""]).Take(2).ToArray();
                lines[line] = memo;
                AppStore.WriteAtomic(t.File, Encoding.UTF8.GetBytes(string.Join('\n', lines.Append(Failed))));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // tried again at the next scan
    }

    /// <summary>Drops a game's memo of candidates tried in vain (<see cref="LookUpAsync"/>'s tried): a key was found for it.</summary>
    public static void Forget(string file)
    {
        try
        {
            if (!File.Exists(file)) return;
            using (new AppStore.PathGate(file)) File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // tried once more in vain at the next scan
    }

    /// <summary>Drops a memo written before only failures were remembered, which may name the candidates that found the
    /// game's stored key; for a game whose stored key no longer opens its files, so they are tried again.</summary>
    public static void ForgetLegacy(string file)
    {
        if (File.Exists(file) && Remembered(file) is not [_, _, Failed, ..]) Forget(file);
    }

    /// <summary>The list: the cached copy, fetched again when it is due (any age for another address); a failed fetch keeps
    /// the cached copy. Problem: why a due fetch failed, null when it wasn't due or worked.</summary>
    public async Task<(IReadOnlyList<KeyEntry>? List, string? Problem)> ListAsync(string url, bool userRequested, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var c = Load();
            var now = clock.GetUtcNow();
            bool Due(DateTimeOffset? at, TimeSpan every) => at is not { } a || now - a < TimeSpan.Zero || now - a >= every;   // a clock set back doesn't hold it off
            var failures = c?.Url == url ? c.Failures : 0;
            var due = c == null || c.Url != url || Due(c.FetchedAt, userRequested ? UserEvery : AutoEvery);
            // after failures: 1 h, 2 h, 4 h ... up to a day between tries; the user's button after 5 minutes
            var wait = failures == 0 ? TimeSpan.Zero : userRequested ? UserRetry : TimeSpan.FromHours(Math.Min(24, Math.Pow(2, Math.Min(failures, 6) - 1)));
            if (!due || !Due(c?.TriedAt, wait))
                return c is { Entries.Length: > 0 } ? (c.Entries, null) : (null, "the last try failed; SCSKiller tries again later");
            c = (c ?? new(url, null, null, 0, [])) with { TriedAt = now };
            Save(c);   // another process sees the attempt
            var (got, problem) = await FetchAsync(url, ct);
            if (got == null)
            {
                // another address's list stays as the fallback, never fresh for this one
                Save(c with { Url = url, FetchedAt = c.Url == url ? c.FetchedAt : null, Failures = failures + 1 });
                return (c.Entries.Length > 0 ? c.Entries : null, problem);
            }
            Save(new(url, now, now, 0, got));
            return (got, null);
        }
        finally { gate.Release(); }
    }

    /// <summary>Why a saved page of <paramref name="length"/> bytes or chars isn't read; null = it is. The app checks the file's
    /// size with it before reading, so a large one is reported as that, not as a page without keys.</summary>
    public static string? TooLarge(long length) => length > MaxBytes ? $"the saved page is larger than {MaxBytes >> 20} MB" : null;

    /// <summary>A copy of the list page the user saved from a browser: parsed like a download and cached as fetched now from
    /// <paramref name="url"/>.</summary>
    public (IReadOnlyList<KeyEntry>? List, string? Problem) Import(string url, string html)
    {
        if (TooLarge(html.Length) is { } large) return (null, large);
        var list = Parse(html);
        if (list.Count == 0) return (null, "the saved page has no keys in it (not the key list?)");
        gate.Wait();
        try { Save(new(url, clock.GetUtcNow(), Load()?.TriedAt, 0, [.. list])); }
        finally { gate.Release(); }
        return (list, null);
    }

    /// <summary>The keys of a file the user collected (<see cref="ParseFile"/>, at most <see cref="MaxImportBytes"/>). They
    /// join keys\imported.json, ahead of those imported before (<see cref="WithImported"/>), for this and later lookups.
    /// Problem: why the file gave nothing (Keys null), or why its keys couldn't be kept there.</summary>
    public ((List<KeyEntry> Named, List<string> Unnamed)? Keys, string? Problem) ImportFile(string path)
    {
        string text;
        try
        {
            if (new FileInfo(path).Length > MaxImportBytes) return (null, $"the file is larger than {MaxImportBytes >> 20} MB");
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, e.Message.TrimEnd('.')); }
        var keys = ParseFile(text);
        if (keys.Named.Count + keys.Unnamed.Count == 0) return (null, "the file has no keys in it (64 hex digits, with or without a name before them)");
        gate.Wait();
        try
        {
            using (new AppStore.PathGate(imported))   // the app and the command line may import at once
            {
                // one that can't be read is never replaced: its keys would be lost
                if (ReadImported() is not { } had) return (keys, $"{imported} can't be read (fix or delete it)");
                var (named, unnamed) = Merge(keys, had);
                AppStore.WriteAtomic(imported, JsonSerializer.SerializeToUtf8Bytes(new ImportedFile(named, unnamed), Json));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (keys, $"saving {imported} failed: {e.Message.TrimEnd('.')}"); }
        finally { gate.Release(); }
        return (keys, null);
    }

    /// <summary>The keys a lookup tries after an import of <paramref name="keys"/>: the file's ahead of those imported before,
    /// as <see cref="ImportFile"/> keeps them (also when keeping them failed).</summary>
    public (KeyEntry[] Named, string[] Unnamed) WithImported((List<KeyEntry> Named, List<string> Unnamed) keys) => Merge(keys, ReadImported() ?? ([], []));

    // named: one per name and key, unnamed: one per key that no named entry has; the file's first
    static (KeyEntry[] Named, string[] Unnamed) Merge((List<KeyEntry> Named, List<string> Unnamed) keys, (KeyEntry[] Named, string[] Unnamed) had)
    {
        KeyEntry[] named = [.. keys.Named.Concat(had.Named).DistinctBy(e => (e.Name, e.Key)).Take(MaxEntries)];
        var taken = named.Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (named, [.. keys.Unnamed.Concat(had.Unnamed).Distinct(StringComparer.OrdinalIgnoreCase).Where(k => !taken.Contains(k)).Take(MaxImportedUnnamed)]);
    }

    /// <summary>The entries imported from files, the latest import's first.</summary>
    public IReadOnlyList<KeyEntry> Imported() => ReadImported()?.Named ?? [];

    /// <summary>The unnamed keys imported from files (a key alone on its line), the latest import's first.</summary>
    public IReadOnlyList<string> ImportedUnnamed() => ReadImported()?.Unnamed ?? [];

    // keys\imported.json, the user's to edit: checked as an import is. Empty without the file, null when it can't be read.
    (KeyEntry[] Named, string[] Unnamed)? ReadImported()
    {
        try
        {
            if (!File.Exists(imported)) return ([], []);
            using var f = new FileStream(imported, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (JsonSerializer.Deserialize<ImportedFile>(f, Json) is not { Entries: not null } file) return null;
            return ([.. file.Entries.Where(e => Valid(e) && !LongHex.IsMatch(e.Name)).Select(e => e with { Key = e.Key.ToUpperInvariant() }).Take(MaxEntries)],
                [.. (file.Unnamed ?? []).Where(k => k is { Length: 64 } && k.All(char.IsAsciiHexDigit)).Select(k => k.ToUpperInvariant()).Distinct().Take(MaxImportedUnnamed)]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>The imported keys to try on a game known by <paramref name="names"/>: the named entries a lookup picks
    /// (<see cref="Candidates"/>), then at most <see cref="MaxUnnamed"/> unnamed keys, named "unnamed key #n" by their place.
    /// ByName: how many are named entries.</summary>
    public static (List<KeyEntry> Tries, int ByName) ImportedCandidates(IReadOnlyList<KeyEntry> named, IReadOnlyList<string> unnamed, IEnumerable<string?> names)
    {
        var mine = Candidates(named, names);
        var keys = mine.Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ([.. mine, .. unnamed.Take(MaxUnnamed).Select((k, i) => new KeyEntry($"unnamed key #{i + 1}", k)).Where(e => !keys.Contains(e.Key))], mine.Count);
    }

    /// <summary>Imported keys for a game known by <paramref name="names"/> (<see cref="ImportedCandidates"/>), each given to
    /// <paramref name="trySet"/>, which stores it only if it opens the game's files: the named entries, then, only if none of
    /// them works, the unnamed keys. <paramref name="tried"/>: remembered as a lookup remembers them (<see cref="LookUpAsync"/>),
    /// so the next scan doesn't try them again for the same stamp. The log names the entry ("unnamed key #3"), never a key.</summary>
    public static KeyLookup TryImported(IReadOnlyList<KeyEntry> named, IReadOnlyList<string> unnamed, IEnumerable<string?> names, Func<string, bool> trySet,
        IProgress<string>? log = null, CancellationToken ct = default, (string File, string Stamp)? tried = null)
    {
        var (tries, byName) = ImportedCandidates(named, unnamed, names);
        string Label(int i) => i < byName ? $"the key imported for \"{tries[i].Name}\"" : tries[i].Name;
        for (var i = 0; i < tries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            log?.Report($"checking {Label(i)}");
            if (!trySet(tries[i].Key)) continue;
            if (tried is { } found) Forget(found.File);
            return new(KeyLookupOutcome.Unlocked, $"Unlocked with {Label(i)}.", tries[i].Name, i + 1);
        }
        if (tried is { } t && tries.Count > 0) Remember(t, 1, Memo(t.Stamp, tries));
        return new(KeyLookupOutcome.NoWorkingKey, tries.Count == 0 ? "No imported key for this game." : $"No working key ({tries.Count} tried).", Tried: tries.Count);
    }

    async Task<(KeyEntry[]? Entries, string? Problem)> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return (null, "the key list's address isn't an https:// address");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd($"SCSKiller/{AppVersion.Current}");
            request.Headers.Accept.ParseAdd("text/html");
            using var r = await http.SendAsync(request, cts.Token);   // buffered: over MaxResponseContentBufferSize is an HttpRequestException
            if (r.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps) return (null, "the key list's address redirected away from https");
            var body = await r.Content.ReadAsStringAsync(cts.Token);
            if (r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable
                && body.Contains("<script", StringComparison.OrdinalIgnoreCase)) return (null, SecurityCheck);
            if (!r.IsSuccessStatusCode) return (null, $"the server answered {(int)r.StatusCode} {r.ReasonPhrase}");
            var list = Parse(body);
            return list.Count > 0 ? ([.. list], null) : (null, "the page has no keys in it (not the key list?)");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, $"no answer within {Timeout.TotalSeconds:F0} s"); }
        catch (HttpRequestException e) { return (null, e.Message.TrimEnd('.')); }
    }

    CacheFile? Load()
    {
        try
        {
            if (!File.Exists(file) || JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(file), Json) is not { Url: not null, Entries: not null } c) return null;
            return c with { Entries = [.. c.Entries.Where(Valid).Take(MaxEntries)] };   // the file is the user's to edit: checked as the page is
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    void Save(CacheFile c) => Write(file, c);   // a failure: used anyway; fetched again when due

    static void Write<T>(string path, T value)
    {
        try { AppStore.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value, Json)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static bool Valid(KeyEntry? e) => e is { Name: not null, Key: { Length: 64 } k } && k.All(char.IsAsciiHexDigit) && ContentFile.Text(e.Name, MaxName) && e.Name.Any(char.IsLetterOrDigit);

    static readonly Regex Scripts = new(@"<(script|style)\b.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex Breaks = new(@"<(?:br|hr|/?(?:li|div|p|ol|ul|dl|dd|dt|tr|td|th|table|pre|blockquote|h[1-6]))\b[^>]*>", RegexOptions.IgnoreCase);
    static readonly Regex Tags = new("<[^>]*>");
    // "Wardogs (playtest)&nbsp; 0x9846...": the key ends the line; any 64-digit run inside the name rejects it below
    static readonly Regex Line = new(@"^(?<name>.{1,120}?)[\s:=\-–—]*(?:0x)?(?<![0-9A-Fa-f])(?<key>[0-9A-Fa-f]{64})$");
    static readonly Regex LongHex = new("[0-9A-Fa-f]{32}");

    /// <summary>The "name key" lines of the topic's first post (the whole page if it has no phpBB post body): block tags and
    /// &lt;br&gt; end lines, other tags go, entities are decoded. Nothing else in the page is used.</summary>
    public static List<KeyEntry> Parse(string html)
    {
        var list = new List<KeyEntry>();
        foreach (var raw in PageText(html).Split('\n'))
        {
            var line = Clean(raw);
            if (line.Length < 66 || Line.Match(line) is not { Success: true } m) continue;
            var name = m.Groups["name"].Value.Trim().TrimEnd(':', '-', '=', '–', '—').Trim();
            var e = new KeyEntry(name, m.Groups["key"].Value.ToUpperInvariant());
            if (!LongHex.IsMatch(name) && Valid(e)) list.Add(e);
            if (list.Count == MaxEntries) break;
        }
        return list;
    }

    static string PageText(string html)
    {
        const string Post = "class=\"postbody\"";
        if (html.IndexOf(Post, StringComparison.Ordinal) is var first and >= 0)   // from the end of its tag to the next post body's
        {
            var end = html.IndexOf(Post, first + Post.Length, StringComparison.Ordinal) is var next and > 0 ? next : html.Length;
            html = html[Math.Min(end, Math.Max(first, html.IndexOf('>', first) + 1))..end];
        }
        return WebUtility.HtmlDecode(Tags.Replace(Breaks.Replace(Scripts.Replace(html, ""), "\n"), ""));
    }

    // no-break and zero-width spaces, control and format characters: the CLI prints names to a terminal. Tabs: kept as
    // column separators when asked.
    static string Clean(string raw, bool tabs = false) =>
        Regex.Replace(string.Concat(raw.Where(ch => (tabs && ch == '\t' || !char.IsControl(ch)) && char.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.Format))
            .Replace(' ', ' '), tabs ? @"[^\S\t]+" : @"\s+", " ").Trim();

    static readonly Regex Markup = new(@"<(?:html|body|br|div|p|li|td|span|table|pre)\b", RegexOptions.IgnoreCase);
    // a key as a word of its own: "0x" optional, no letter or digit touching it
    static readonly Regex KeyWord = new(@"(?<![0-9A-Za-z])(?:0[xX])?(?<key>[0-9A-Fa-f]{64})(?![0-9A-Za-z])");
    static readonly Regex NameColumn = new(@"^(?:game|name|title|game ?name)$", RegexOptions.IgnoreCase), KeyColumn = new(@"^(?:key|aes|aes[ _]?key|pak[ _]?key|hex)$", RegexOptions.IgnoreCase);
    static readonly char[] Separators = [' ', '\t', ':', '=', '-', '–', '—', ',', ';', '|', '"', '\''];

    /// <summary>The keys of a file the user collected, untrusted: a saved page (its text as <see cref="Parse"/> takes it), lines
    /// of "name key" (space, tab, ':', '=', ',', ';', '-' or '|' between, "0x" optional, "key name" too) or of a key alone, CSV
    /// with a header naming its name and key columns, or JSON (an array of {name, key} or of lines, an object of name: key).
    /// A line with two keys, or whose name isn't plain text, is skipped; at most <see cref="MaxEntries"/>. Named: one per name
    /// and key, in file order; unnamed: one per key, none that a named entry has.</summary>
    public static (List<KeyEntry> Named, List<string> Unnamed) ParseFile(string text)
    {
        var named = new List<KeyEntry>();
        var unnamed = new List<string>();
        void Add(string? name, string key)
        {
            if (named.Count + unnamed.Count >= MaxEntries) return;
            name = Regex.Replace(name ?? "", @"\s+", " ").Trim(Separators);
            if (name.Length == 0) unnamed.Add(key);
            else if (new KeyEntry(name, key) is var e && !LongHex.IsMatch(name) && Valid(e)) named.Add(e);
        }
        var json = false;
        if (text.TrimStart() is ['[' or '{', ..])
            try
            {
                using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                FromJson(doc.RootElement, Add, 0);
                json = true;
            }
            catch (JsonException) { }   // read as lines
        if (!json) Lines(Markup.IsMatch(text) ? PageText(text) : text, Add);
        var keys = named.Select(e => e.Key).ToHashSet();
        return ([.. named.DistinctBy(e => (e.Name, e.Key))], [.. unnamed.Distinct().Where(k => !keys.Contains(k))]);
    }

    static void Lines(string text, Action<string?, string> add)
    {
        (char Sep, int Name, int Key)? columns = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = Clean(raw, tabs: true);
            var found = KeyWord.Matches(line);
            if (found.Count == 0) { columns = Header(line) ?? columns; continue; }
            if (found.Count > 1) continue;
            var m = found[0];
            var key = m.Groups["key"].Value.ToUpperInvariant();
            if (columns is { } c && Fields(line, c.Sep) is var f && f.Count > Math.Max(c.Name, c.Key) && KeyOf(f[c.Key]) == key) { add(f[c.Name], key); continue; }
            var before = line[..m.Index].Trim(Separators);
            add(before.Length > 0 ? before : line[(m.Index + m.Length)..], key);
        }
    }

    static (char Sep, int Name, int Key)? Header(string line)
    {
        foreach (var sep in "\t,;|")
            if (line.Contains(sep) && Fields(line, sep) is var f && f.FindIndex(NameColumn.IsMatch) is var n and >= 0 && f.FindIndex(KeyColumn.IsMatch) is var k and >= 0)
                return (sep, n, k);
        return null;
    }

    // CSV fields: quoted ones may hold the separator, "" is a quote
    static List<string> Fields(string line, char sep)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
            if (line[i] == '"')
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') field.Append(line[++i]);
                else quoted = !quoted;
            else if (line[i] == sep && !quoted) { fields.Add(field.ToString().Trim()); field.Clear(); }
            else field.Append(line[i]);
        fields.Add(field.ToString().Trim());
        return fields;
    }

    static string? KeyOf(string? value) => value?.Trim(Separators) is { } v && KeyWord.Match(v) is { Success: true } m && m.Length == v.Length ? m.Groups["key"].Value.ToUpperInvariant() : null;

    static void FromJson(JsonElement e, Action<string?, string> add, int depth)
    {
        if (depth > 4) return;
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) Lines(item.GetString()!, add);   // "name 0x..." or a key alone
                else FromJson(item, add, depth + 1);
            return;
        }
        if (e.ValueKind != JsonValueKind.Object) return;
        static string? Prop(JsonElement o, Regex name) => o.EnumerateObject().FirstOrDefault(p => name.IsMatch(p.Name) && p.Value.ValueKind == JsonValueKind.String).Value is
            { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
        if (Prop(e, KeyColumn) is { } own)   // {name, key}
        {
            if (KeyOf(own) is { } k) add(Prop(e, NameColumn), k);
            return;
        }
        foreach (var p in e.EnumerateObject())   // name: key, name: {key}, or a list under any name
            if (p.Value.ValueKind == JsonValueKind.String) { if (KeyOf(p.Value.GetString()) is { } k) add(p.Name, k); }
            else if (p.Value.ValueKind == JsonValueKind.Object && Prop(p.Value, KeyColumn) is { } inner) { if (KeyOf(inner) is { } k) add(Prop(p.Value, NameColumn) ?? p.Name, k); }
            else FromJson(p.Value, add, depth + 1);
    }

    /// <summary>The entries to try for a game known by <paramref name="names"/>, compared as <see cref="UnrealReader.Norm"/>
    /// does: exact names first, then variants that start with a game name ("Wardogs (playtest)"), then entries a game name
    /// starts with ("Game" for "Game Deluxe Edition"); page order within each, one per key, at most <paramref name="max"/>.</summary>
    public static List<KeyEntry> Candidates(IEnumerable<KeyEntry> list, IEnumerable<string?> names, int max = MaxCandidates)
    {
        var mine = names.OfType<string>().Select(UnrealReader.Norm).Where(n => n.Length >= 3).Distinct().ToList();
        if (mine.Count == 0) return [];
        int Rank(string n) => mine.Contains(n) ? 0
            : mine.Any(m => m.Length >= 5 && n.StartsWith(m, StringComparison.Ordinal)) ? 1
            : n.Length >= 6 && mine.Any(m => m.StartsWith(n, StringComparison.Ordinal)) ? 2 : -1;
        return list.Select((e, i) => (e, i, r: Rank(UnrealReader.Norm(e.Name)))).Where(x => x.r >= 0).OrderBy(x => x.r).ThenBy(x => x.i)
            .Select(x => x.e).DistinctBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }
}
