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

/// <summary>The community's list of Unreal pak keys: the first post of a forum topic (Settings.KeyListUrl), fetched over
/// HTTPS on the user's request or, with Settings.LookUpKeysOnline, after a scan finds an encrypted game without a key. A
/// plain GET of the page: nothing about the user or their games is sent; names are matched here. The page is untrusted:
/// only "name 0x&lt;64 hex&gt;" lines are taken from it, and each key is used only if it opens the game's files
/// (<see cref="UnrealKeys.Set"/>). The parsed list is kept in keys\collection.json with when it was fetched: refetched at
/// most every <see cref="AutoEvery"/>, on the user's request every <see cref="UserEvery"/>; a failure backs off and the
/// cached copy is used.</summary>
public sealed class KeyCollection
{
    public const string DefaultUrl = "https://cs.rin.ru/forum/viewtopic.php?f=10&t=100672";
    public const int MaxBytes = 4 << 20, MaxEntries = 20_000, MaxCandidates = 20, MaxName = 120;
    public static readonly TimeSpan AutoEvery = TimeSpan.FromHours(24), UserEvery = TimeSpan.FromHours(1), UserRetry = TimeSpan.FromMinutes(5),
        Timeout = TimeSpan.FromSeconds(20);
    public const string SecurityCheck = "the forum answered with a browser security check, which SCSKiller doesn't take: open the list in your browser, save the page (Ctrl+S) and load the saved page";

    sealed record CacheFile(string Url, DateTimeOffset? FetchedAt, DateTimeOffset? TriedAt, int Failures, KeyEntry[] Entries);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly string file;
    readonly HttpClient http;
    readonly TimeProvider clock;
    readonly SemaphoreSlim gate = new(1, 1);   // a scan's pass and the user's button: one fetch, not two

    public KeyCollection(string dataDir, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        file = Path.Combine(dataDir, "keys", "collection.json");
        http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, MaxAutomaticRedirections = 5 }, handler == null)
            { Timeout = System.Threading.Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaxBytes };
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>When the cached list was fetched (or loaded from a saved page); null = none.</summary>
    public DateTimeOffset? FetchedAt => Load()?.FetchedAt;

    /// <summary>Looks the game up under <paramref name="names"/> and gives each candidate key (<see cref="Candidates"/>), in
    /// order, to <paramref name="trySet"/>, which stores it only if it opens the game's files; the first it takes wins.
    /// <paramref name="tried"/>: a file that remembers the candidates last tried for the game with a stamp of its files; a
    /// lookup that isn't <paramref name="userRequested"/> skips the same candidates for the same stamp (AlreadyTried).
    /// <paramref name="savedPage"/>: the list as the user saved it from a browser, used instead of fetching and cached.
    /// The log names list entries, never a key.</summary>
    public async Task<KeyLookup> LookUpAsync(string url, IEnumerable<string?> names, Func<string, bool> trySet, bool userRequested,
        (string File, string Stamp)? tried = null, IProgress<string>? log = null, CancellationToken ct = default, string? savedPage = null)
    {
        var (list, problem) = savedPage != null ? Import(url, savedPage) : await ListAsync(url, userRequested, ct);
        if (list == null) return new(KeyLookupOutcome.FetchFailed, $"Couldn't get the key list: {problem}.");
        var stale = problem != null ? $" (the list couldn't be refreshed: {problem}; used the saved copy{(FetchedAt is { } at ? $" from {at.ToLocalTime():d MMM yyyy}" : "")})" : "";
        var candidates = Candidates(list, names);
        if (candidates.Count == 0) return new(KeyLookupOutcome.NoWorkingKey, $"The key list ({list.Count:N0} entries) has no entry for this game{stale}.");
        var memo = tried is { } t ? $"{t.Stamp}|{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', candidates.Select(c => c.Key.ToUpperInvariant())))))}" : null;
        if (!userRequested && memo != null && Remembered(tried!.Value.File) == memo)
            return new(KeyLookupOutcome.AlreadyTried, "The keys listed for this game were already tried.");
        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            log?.Report($"checking the key listed for \"{candidates[i].Name}\"");
            if (!trySet(candidates[i].Key)) continue;
            Remember(tried, memo);
            return new(KeyLookupOutcome.Unlocked, $"Unlocked with the key listed for \"{candidates[i].Name}\"{stale}.", candidates[i].Name, i + 1);
        }
        Remember(tried, memo);
        return new(KeyLookupOutcome.NoWorkingKey, candidates.Count == 1
            ? $"The key listed for \"{candidates[0].Name}\" doesn't open this game's files{stale}."
            : $"None of the {candidates.Count} keys listed for this game ({string.Join(", ", candidates.Select(c => c.Name).Distinct().Take(4))}) opens its files{stale}.", Tried: candidates.Count);
    }

    static string? Remembered(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static void Remember((string File, string Stamp)? tried, string? memo)
    {
        if (tried is not { } t || memo == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(t.File)!);
            File.WriteAllText(t.File, memo);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // tried again at the next scan
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

    /// <summary>A copy of the list page the user saved from a browser: parsed like a download and cached as fetched now from
    /// <paramref name="url"/>.</summary>
    public (IReadOnlyList<KeyEntry>? List, string? Problem) Import(string url, string html)
    {
        if (html.Length > MaxBytes) return (null, $"the saved page is larger than {MaxBytes >> 20} MB");
        var list = Parse(html);
        if (list.Count == 0) return (null, "the saved page has no keys in it (not the key list?)");
        gate.Wait();
        try { Save(new(url, clock.GetUtcNow(), Load()?.TriedAt, 0, [.. list])); }
        finally { gate.Release(); }
        return (list, null);
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

    void Save(CacheFile c)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(c, Json));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // used anyway; fetched again when due
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
        const string Post = "class=\"postbody\"";
        if (html.IndexOf(Post, StringComparison.Ordinal) is var first and >= 0)   // from the end of its tag to the next post body's
        {
            var end = html.IndexOf(Post, first + Post.Length, StringComparison.Ordinal) is var next and > 0 ? next : html.Length;
            html = html[Math.Min(end, Math.Max(first, html.IndexOf('>', first) + 1))..end];
        }
        var text = WebUtility.HtmlDecode(Tags.Replace(Breaks.Replace(Scripts.Replace(html, ""), "\n"), ""));
        var list = new List<KeyEntry>();
        foreach (var raw in text.Split('\n'))
        {
            // no-break and zero-width spaces, control and format characters: the CLI prints names to a terminal
            var line = Regex.Replace(string.Concat(raw.Where(ch => !char.IsControl(ch) && char.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.Format))
                .Replace(' ', ' '), @"\s+", " ").Trim();
            if (line.Length < 66 || Line.Match(line) is not { Success: true } m) continue;
            var name = m.Groups["name"].Value.Trim().TrimEnd(':', '-', '=', '–', '—').Trim();
            var e = new KeyEntry(name, m.Groups["key"].Value.ToUpperInvariant());
            if (!LongHex.IsMatch(name) && Valid(e)) list.Add(e);
            if (list.Count == MaxEntries) break;
        }
        return list;
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
