using System.Net;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.Unreal;
using SCSKiller.Tests.Platform;

namespace SCSKiller.Tests.Unreal;

/// <summary>The key list lookup on a synthetic page shaped like the forum topic (spoilers, &lt;br&gt; and &lt;li&gt; lines,
/// entities, variants, an "Older keys" spoiler, replies). Every key here is fake; nothing goes to the network.</summary>
public class KeyCollectionTests : IDisposable
{
    const string Url = "https://keys.test/topic?t=1";
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-keylist-test-" + Guid.NewGuid().ToString("N")[..8]);
    readonly CommunityTests.Clock _clock = new();

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    static string K(int i) => Convert.ToHexString(SHA256.HashData([(byte)i]));   // fake keys: 64 hex digits

    static readonly string Page = $$"""
        <html><head><title>Collection of keys</title><script>var s = "Script Game 0x{{K(90)}}";</script></head><body>
        <table><tr><td><div class="postbody">Intro: some keys may be outdated.<br><span style="font-weight: bold">Read this first</span><br>
        <div class="spoiler"><div style="margin-bottom: 2px;"><input value="Show" onclick="if(this.closest('.spoiler')) {}" type="button"><b>Keys</b></div>
        <div class="quotecontent"><div style="display: none;"><ol><li class="li1">Alpha Game&nbsp; &nbsp;0x{{K(1)}}</li><li class="li2">Bravo &amp; Charlie: Part II&nbsp;0x{{K(2)}}</li><li class="li1">Wardogs&nbsp;0x{{K(3)}}</li><li class="li2">Wardogs (playtest)&nbsp; 0x{{K(4)}}</li><li class="li1">Echo&#8203;&#8203;Lake - {{K(5).ToLowerInvariant()}}</li></ol></div></div></div>
        Delta Run: 0x{{K(6)}}<br>Not a key 0x123ABC<br>Too long 0x{{K(7)}}AB<br>Two keys 0x{{K(8)}} 0x{{K(9)}}<br><a href="https://elsewhere.test/">a link 0x{{K(10)}}</a>
        <div class="spoiler"><div><input value="Show" type="button"><b>Older keys</b></div><div class="quotecontent"><div style="display: none;">Wardogs (old playtest) 0x{{K(11)}}<br>Alpha Game 0x{{K(12)}}<br></div></div></div>
        </div></td></tr></table>
        <div class="postbody"><br>_________________<br>Wardogs 0x{{K(13)}}</div>
        <div class="postbody">Thanks! Wardogs (new) 0x{{K(14)}}</div>
        </body></html>
        """;

    [Fact]
    public void Parse_takes_the_name_key_lines_of_the_first_post_only()
    {
        var list = KeyCollection.Parse(Page);
        Assert.Equal([
            new("Alpha Game", K(1)), new("Bravo & Charlie: Part II", K(2)), new("Wardogs", K(3)), new("Wardogs (playtest)", K(4)),
            new("EchoLake", K(5)), new("Delta Run", K(6)), new("a link", K(10)), new("Wardogs (old playtest)", K(11)), new("Alpha Game", K(12)),
        ], list);   // no script, no reply, no short, long or doubled key; the link's text is only text
    }

    [Fact]
    public void Parse_of_a_page_without_post_bodies_reads_its_lines_and_rejects_markup_in_names()
    {
        var list = KeyCollection.Parse($"Foxtrot&lt;b&gt; 0x{K(20)}\nGolf\u0007 Two = {K(21)}\n<p>{new string('n', 130)} 0x{K(22)}</p>\n:: 0x{K(23)}");
        Assert.Equal([new("Foxtrot<b>", K(20)), new("Golf Two", K(21))], list);   // decoded entities stay text; a 130-char name and no name are refused
    }

    [Fact]
    public void Candidates_are_exact_names_then_variants_then_shorter_names_in_page_order()
    {
        var list = KeyCollection.Parse(Page);
        Assert.Equal(["Wardogs", "Wardogs (playtest)", "Wardogs (old playtest)"],
            KeyCollection.Candidates(list, ["WARDOGS", "Wardogs", null]).Select(e => e.Name));
        Assert.Equal([K(2)], KeyCollection.Candidates(list, ["BRAVO & CHARLIE: PART 2"]).Select(e => e.Key));   // roman numerals, '&', punctuation
        Assert.Equal([K(1), K(12)], KeyCollection.Candidates(list, ["Alpha Game™ Deluxe Edition"]).Select(e => e.Key));   // the older key too
        Assert.Empty(KeyCollection.Candidates(list, ["Ward"]));   // too short to stand for a variant
        Assert.Empty(KeyCollection.Candidates(list, ["Zulu"]));

        var many = Enumerable.Range(0, 30).Select(i => new KeyEntry($"Hotel (build {i})", K(100 + i))).Prepend(new("Hotel", K(1))).Append(new("Hotel", K(1))).ToList();
        var picked = KeyCollection.Candidates(many, ["Hotel"]);
        Assert.Equal(KeyCollection.MaxCandidates, picked.Count);
        Assert.Equal(K(1), picked[0].Key);
        Assert.Single(picked, e => e.Key == K(1));   // one try per key
    }

    sealed class Server(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Requests;
        public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            Last = r;
            return Task.FromResult(answer());
        }
    }

    static HttpResponseMessage Html(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    [Fact]
    public async Task The_list_is_fetched_at_most_daily_and_on_request_at_most_hourly()
    {
        var server = new Server(() => Html(Page));
        var keys = new KeyCollection(_dir, server, _clock);
        var (list, problem) = await keys.ListAsync(Url, false);
        Assert.Equal((9, (string?)null, 1), (list!.Count, problem, server.Requests));
        Assert.StartsWith("SCSKiller-fork-dkflint723/", server.Last!.Headers.UserAgent.ToString());   // this unofficial build's name
        Assert.False(server.Last.Headers.Contains("Cookie"));   // a plain GET: no cookie, nothing about the user
        Assert.True(File.Exists(Path.Combine(_dir, "keys", "collection.json")));

        _clock.Now += TimeSpan.FromMinutes(50);
        await keys.ListAsync(Url, true);
        await keys.ListAsync(Url, false);
        Assert.Equal(1, server.Requests);   // fresh: the cached copy
        _clock.Now += TimeSpan.FromMinutes(11);
        await keys.ListAsync(Url, false);
        Assert.Equal(1, server.Requests);   // a scan waits a day
        await keys.ListAsync(Url, true);
        Assert.Equal(2, server.Requests);   // the user's button after an hour
        _clock.Now += TimeSpan.FromHours(24);
        Assert.Equal(9, (await new KeyCollection(_dir, server, _clock).ListAsync(Url, false)).List!.Count);   // another process reads the cache
        Assert.Equal(3, server.Requests);
        await keys.ListAsync("https://mirror.test/keys", false);
        Assert.Equal(4, server.Requests);   // another address: fetched whatever the age
    }

    [Fact]
    public async Task A_failed_fetch_backs_off_and_falls_back_to_the_cache()
    {
        var down = false;
        var server = new Server(() => down ? Html("oops", HttpStatusCode.InternalServerError) : Html(Page));
        var keys = new KeyCollection(_dir, server, _clock);
        await keys.ListAsync(Url, false);
        down = true;
        _clock.Now += TimeSpan.FromHours(25);
        var (list, problem) = await keys.ListAsync(Url, false);
        Assert.Equal(9, list!.Count);   // the cached copy
        Assert.Contains("500", problem);
        Assert.Equal(2, server.Requests);
        _clock.Now += TimeSpan.FromMinutes(59);
        Assert.Null((await keys.ListAsync(Url, false)).Problem);
        Assert.Equal(2, server.Requests);   // 1 h after the first failure
        _clock.Now += TimeSpan.FromMinutes(2);
        await keys.ListAsync(Url, false);
        Assert.Equal(3, server.Requests);
        _clock.Now += TimeSpan.FromMinutes(90);
        await keys.ListAsync(Url, false);
        Assert.Equal(3, server.Requests);   // 2 h after the second
        await keys.ListAsync(Url, true);
        Assert.Equal(4, server.Requests);   // the user's button: 5 minutes after the last try
        await keys.ListAsync(Url, true);
        Assert.Equal(4, server.Requests);
        down = false;
        _clock.Now += TimeSpan.FromHours(5);
        Assert.Equal((9, (string?)null), ((await keys.ListAsync(Url, false)).List!.Count, (await keys.ListAsync(Url, false)).Problem));
        Assert.Equal(5, server.Requests);
    }

    [Fact]
    public async Task Fetch_failures_say_why()
    {
        async Task<(bool Got, string? Problem, int Requests)> Get(Func<HttpResponseMessage> answer, string url = Url)
        {
            var server = new Server(answer);
            var (list, problem) = await new KeyCollection(Path.Combine(_dir, Guid.NewGuid().ToString("N")), server, _clock).ListAsync(url, true);
            return (list != null, problem, server.Requests);
        }
        Assert.Equal((false, KeyCollection.SecurityCheck, 1), await Get(() => Html("<html><script>document.cookie = 'token';</script><noscript>401</noscript></html>", HttpStatusCode.Unauthorized)));
        Assert.Equal((false, "the page has no keys in it (not the key list?)", 1), await Get(() => Html("<html>maintenance</html>")));
        Assert.Equal((false, "the key list's address isn't an https:// address", 0), await Get(() => Html(Page), "http://keys.test/topic"));
        var (got, problem, _) = await Get(() => Html(new string('x', KeyCollection.MaxBytes + 1)));
        Assert.False(got);
        Assert.NotNull(problem);
    }

    static Game FakeGame(string dir, string name) => new("test:keys", name, Store.Other, Path.Combine(dir, "install"), Path.Combine(dir, "install", "Game.exe"));

    [Fact]
    public async Task A_lookup_stores_only_a_key_that_opens_the_files_trying_candidates_in_order()
    {
        var data = Path.Combine(_dir, "data");
        var game = FakeGame(_dir, "WARDOGS");
        var stored = new UnrealKeys(data);
        var tried = new List<string>();
        var log = new List<string>();
        bool TrySet(string key)
        {
            tried.Add(key);
            return stored.Set(game, key, k => k.KeyString.EndsWith(K(4), StringComparison.OrdinalIgnoreCase));   // the playtest key opens the files
        }
        var keys = new KeyCollection(_dir, new Server(() => Html(Page)), _clock);
        var r = await keys.LookUpAsync(Url, [game.Name], TrySet, true, log: new SyncLog(log));
        Assert.Equal((KeyLookupOutcome.Unlocked, "Wardogs (playtest)", 2), (r.Outcome, r.Entry, r.Tried));
        Assert.Equal([K(3), K(4)], tried);
        Assert.Equal("0x" + K(4), stored.Stored(game)!.KeyString, ignoreCase: true);
        foreach (var text in log.Append(r.Message))
            foreach (var k in Enumerable.Range(0, 30).Select(K))
                Assert.DoesNotContain(k, text, StringComparison.OrdinalIgnoreCase);   // names only, never a key
    }

    [Fact]
    public async Task A_lookup_without_a_working_key_stores_nothing_and_respects_the_cap()
    {
        var page = "<div class=\"postbody\">" + string.Join("<br>", Enumerable.Range(0, 30).Select(i => $"Hotel (build {i}) 0x{K(100 + i)}")) + "</div>";
        var game = FakeGame(_dir, "Hotel");
        var stored = new UnrealKeys(Path.Combine(_dir, "data"));
        var calls = 0;
        var r = await new KeyCollection(_dir, new Server(() => Html(page)), _clock)
            .LookUpAsync(Url, [game.Name], k => { calls++; return stored.Set(game, k, _ => false); }, true);
        Assert.Equal((KeyLookupOutcome.NoWorkingKey, KeyCollection.MaxCandidates, KeyCollection.MaxCandidates), (r.Outcome, r.Tried, calls));
        Assert.Null(stored.Stored(game));
        Assert.Contains("None of the 20 keys", r.Message);

        var none = await new KeyCollection(_dir, new Server(() => Html(page)), _clock).LookUpAsync(Url, ["Zulu"], _ => throw new InvalidOperationException(), true);
        Assert.Equal(KeyLookupOutcome.NoWorkingKey, none.Outcome);
        Assert.Contains("has no entry for this game", none.Message);
    }

    [Fact]
    public async Task A_scan_lookup_skips_candidates_already_tried_for_the_same_exe()
    {
        var keys = new KeyCollection(_dir, new Server(() => Html(Page)), _clock);
        var memo = Path.Combine(_dir, "data", "games", "test_keys", "aes.lookup");
        var calls = 0;
        bool Fail(string _) { calls++; return false; }
        Assert.Equal(KeyLookupOutcome.NoWorkingKey, (await keys.LookUpAsync(Url, ["Wardogs"], Fail, false, (memo, "exe-1"))).Outcome);
        Assert.Equal(3, calls);
        Assert.Equal(KeyLookupOutcome.AlreadyTried, (await keys.LookUpAsync(Url, ["Wardogs"], Fail, false, (memo, "exe-1"))).Outcome);
        Assert.Equal(3, calls);
        Assert.DoesNotContain(K(3), File.ReadAllText(memo), StringComparison.OrdinalIgnoreCase);   // a hash of the candidates, not the keys
        await keys.LookUpAsync(Url, ["Wardogs"], Fail, true, (memo, "exe-1"));
        Assert.Equal(6, calls);   // the user's button tries again
        await keys.LookUpAsync(Url, ["Wardogs"], Fail, false, (memo, "exe-2"));
        Assert.Equal(9, calls);   // a game update: again
    }

    [Fact]
    public async Task A_saved_page_is_read_like_a_download_and_counts_as_fresh()
    {
        var server = new Server(() => Html("<html><script></script></html>", HttpStatusCode.Unauthorized));
        var keys = new KeyCollection(_dir, server, _clock);
        var failed = await keys.LookUpAsync(Url, ["Wardogs"], _ => false, true);
        Assert.Equal(KeyLookupOutcome.FetchFailed, failed.Outcome);
        Assert.Contains("security check", failed.Message);
        var r = await keys.LookUpAsync(Url, ["Wardogs"], k => k == K(11), true, savedPage: Page);
        Assert.Equal((KeyLookupOutcome.Unlocked, "Wardogs (old playtest)"), (r.Outcome, r.Entry));
        _clock.Now += TimeSpan.FromHours(2);
        Assert.Equal(9, (await keys.ListAsync(Url, false)).List!.Count);
        Assert.Equal(1, server.Requests);   // the saved copy is used for a day
    }

    [Fact]
    public void A_key_file_of_lines_takes_every_separator_bare_keys_and_skips_junk()
    {
        var (named, unnamed) = KeyCollection.ParseFile(string.Join("\r\n",
            "# my keys", "", $"Alpha Game 0x{K(1)}", $"Bravo: {K(2)}", $"Charlie = 0x{K(3).ToLowerInvariant()}", $"Delta,0x{K(4)}", $"Echo;{K(5)}",
            $"Foxtrot - 0x{K(6)}", $"Golf | {K(7)}", $"Hotel\t{K(8)}", $"0x{K(9)}", $"  {K(10)}  ", $"Alpha Game 0x{K(1)}", $"0x{K(1)}",
            $"Two keys 0x{K(11)} 0x{K(12)}", "Short 0x1234ABCD", $"Long 0x{K(13)}AB", $"{new string('n', 130)} 0x{K(15)}", $"Hash 0x{K(16)[..40]} 0x{K(17)}",
            $"0x{K(18)} India", $"Juliett\u0007 0x{K(19)}"));
        Assert.Equal([
            new("Alpha Game", K(1)), new("Bravo", K(2)), new("Charlie", K(3)), new("Delta", K(4)), new("Echo", K(5)), new("Foxtrot", K(6)),
            new("Golf", K(7)), new("Hotel", K(8)), new("India", K(18)), new("Juliett", K(19)),
        ], named);   // one per name and key; two keys, a short or long one, a 130-char name, a hex name: skipped; control characters go
        Assert.Equal([K(9), K(10)], unnamed);   // a key alone that a named entry has isn't unnamed too
    }

    [Fact]
    public void A_key_file_as_csv_json_or_a_saved_page()
    {
        var csv = KeyCollection.ParseFile($"appid,Game,AES Key,notes\n2288340,\"Bravo \"\"Charlie\"\", II\",0x{K(20)},ok\n1,Delta,{K(21)},\n2,,{K(22)},no name");
        Assert.Equal([new("Bravo \"Charlie\", II", K(20)), new("Delta", K(21))], csv.Named);   // by the header's columns: not the app id, quotes kept as text
        Assert.Equal([K(22)], csv.Unnamed);

        var array = KeyCollection.ParseFile($$"""
            [ {"name": "Alpha", "key": "0x{{K(30)}}"}, {"Game": "Bravo", "aes": "{{K(31)}}"}, {"key": "{{K(32)}}"}, "Charlie 0x{{K(33)}}", "{{K(34)}}",
              {"name": "Bad", "key": "0x1234"}, {"name": "<b>Odd</b>", "key": "{{K(35)}}", "extra": [1, 2]}, 7, null, ]
            """);
        Assert.Equal([new("Alpha", K(30)), new("Bravo", K(31)), new("Charlie", K(33)), new("<b>Odd</b>", K(35))], array.Named);   // names stay text
        Assert.Equal([K(32), K(34)], array.Unnamed);

        var map = KeyCollection.ParseFile($$"""{ "Delta": "0x{{K(36)}}", "Echo": {"key": "{{K(37)}}"}, "more": [{"name": "Foxtrot", "key": "{{K(38)}}"}], "n": 5, "Golf": "x" }""");
        Assert.Equal([new("Delta", K(36)), new("Echo", K(37)), new("Foxtrot", K(38))], map.Named);

        var page = KeyCollection.ParseFile(Page);
        Assert.Equal(KeyCollection.Parse(Page), page.Named);   // as Load saved page reads it: the first post's lines
        Assert.Empty(page.Unnamed);
    }

    /// <summary>A label before a key ("AES Key:", AESDumpster's "[+] Found AES key:", FModel's "mainKey") names no game: the
    /// key is unnamed, so it is tried on every encrypted game, not on none. A list number before a name isn't part of it.</summary>
    [Fact]
    public void A_label_before_a_key_leaves_it_unnamed_and_a_list_number_goes()
    {
        var lines = KeyCollection.ParseFile(string.Join("\n", $"AES Key: 0x{K(40)}", $"Key = 0x{K(41)}", $"[+] Found AES key: 0x{K(42)}", $"Main Key {K(47)}",
            $"1. Hogwarts Legacy: 0x{K(43)}", $"12) Alpha: {K(44)}", $"#3 Bravo 0x{K(45)}", $"7 Days to Die 0x{K(46)}", $"Key: 0x{K(48)} | Key Entropy: 3.56"));
        Assert.Equal([new("Hogwarts Legacy", K(43)), new("Alpha", K(44)), new("Bravo", K(45)), new("7 Days to Die", K(46))], lines.Named);
        Assert.Equal([K(40), K(41), K(42), K(47), K(48)], lines.Unnamed);
        Assert.Equal(K(43), KeyCollection.ImportedCandidates(lines.Named, lines.Unnamed, ["Hogwarts Legacy"]).Tries[0].Key);

        var fmodel = KeyCollection.ParseFile($$"""{"mainKey": "0x{{K(50)}}", "dynamicKeys": [{"guid": "1234", "key": "0x{{K(51)}}"}]}""");
        Assert.Empty(fmodel.Named);
        Assert.Equal([K(50), K(51)], fmodel.Unnamed);
        var api = KeyCollection.ParseFile($$$"""{"status": 200, "data": {"build": "x", "mainKey": "{{{K(52)}}}"}}""");
        Assert.Equal([K(52)], api.Unnamed);
        var games = KeyCollection.ParseFile($$"""[{"game": "Hogwarts Legacy", "mainKey": "0x{{K(53)}}"}]""");
        Assert.Equal([new("Hogwarts Legacy", K(53))], games.Named);   // the object's name column names it
    }

    /// <summary>A page whose charset .NET doesn't know (or a typo) is read as UTF-8, not failed without a backoff.</summary>
    [Theory]
    [InlineData("windows-1251")]
    [InlineData("utf-99")]
    [InlineData("iso-8859-1")]
    public async Task A_page_in_an_unknown_charset_is_read(string charset)
    {
        var server = new Server(() => { var m = Html(Page); m.Content.Headers.ContentType!.CharSet = charset; return m; });
        var (list, problem) = await new KeyCollection(_dir, server, _clock).ListAsync(Url, true);
        Assert.Equal((9, (string?)null), (list?.Count, problem));
    }

    /// <summary>A saved page is cached before the imported keys are tried, so it serves the other games' lookups also when
    /// an imported key unlocks this one.</summary>
    [Fact]
    public async Task A_saved_page_is_kept_when_an_imported_key_unlocks_the_game()
    {
        var server = new Server(() => Html("<html><script></script></html>", HttpStatusCode.Unauthorized));
        var keys = new KeyCollection(_dir, server, _clock);
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "mine.txt");
        File.WriteAllText(file, $"Wardogs 0x{K(60)}");
        Assert.Null(keys.ImportFile(file).Problem);
        var r = await keys.LookUpAsync(Url, ["Wardogs"], k => k == K(60), true, savedPage: Page);
        Assert.Equal((KeyLookupOutcome.Unlocked, "Wardogs"), (r.Outcome, r.Entry));
        Assert.Equal(9, (await keys.ListAsync(Url, false)).List!.Count);
        Assert.Equal(0, server.Requests);
    }

    /// <summary>A saved page over 4 MB ("Webpage, Complete") says so, instead of passing for a page without keys.</summary>
    [Fact]
    public void A_saved_page_too_large_says_so()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "page.html");
        File.WriteAllText(path, Page + new string(' ', KeyCollection.MaxBytes));
        var (page, problem) = KeyCollection.ReadSavedPage(path);
        Assert.Null(page);
        Assert.Contains("larger than 4 MB: save it as 'Webpage, HTML only'", problem);
        File.WriteAllText(path, Page);
        Assert.Equal((Page, (string?)null), KeyCollection.ReadSavedPage(path));
    }

    static string Many(int i) => Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(i)));

    [Fact]
    public void A_key_file_import_is_capped_and_refuses_oversized_or_keyless_files()
    {
        var (named, _) = KeyCollection.ParseFile(string.Join("\n", Enumerable.Range(0, KeyCollection.MaxEntries + 5).Select(i => $"Game {i} 0x{Many(i)}")));
        Assert.Equal(KeyCollection.MaxEntries, named.Count);

        Directory.CreateDirectory(_dir);
        var keys = new KeyCollection(_dir, new Server(() => Html(Page)), _clock);
        var big = Path.Combine(_dir, "big.txt");
        File.WriteAllText(big, $"Alpha 0x{K(1)}\n" + new string(' ', KeyCollection.MaxImportBytes));
        Assert.Equal((false, "the file is larger than 16 MB"), (keys.ImportFile(big).Keys != null, keys.ImportFile(big).Problem));
        var empty = Path.Combine(_dir, "empty.txt");
        File.WriteAllText(empty, "nothing 0x1234\n<p>here</p>");
        Assert.Null(keys.ImportFile(empty).Keys);
        Assert.NotNull(keys.ImportFile(Path.Combine(_dir, "missing.txt")).Problem);
        Assert.Empty(keys.Imported());
    }

    [Fact]
    public void An_import_tries_named_entries_before_unnamed_keys_and_stores_only_one_that_opens_the_files()
    {
        var game = FakeGame(_dir, "WARDOGS");
        var stored = new UnrealKeys(Path.Combine(_dir, "data"));
        var tried = new List<string>();
        var log = new List<string>();
        List<KeyEntry> named = [new("Wardogs", K(3)), new("Alpha Game", K(1)), new("Wardogs (playtest)", K(4))];
        List<string> unnamed = [K(9), K(10), K(11)];
        bool TrySet(string key) { tried.Add(key); return stored.Set(game, key, k => k.KeyString.EndsWith(K(10), StringComparison.OrdinalIgnoreCase)); }
        var r = KeyCollection.TryImported(named, unnamed, [game.Name], TrySet, new SyncLog(log));
        Assert.Equal((KeyLookupOutcome.Unlocked, "unnamed key #2", 4), (r.Outcome, r.Entry, r.Tried));
        Assert.Equal([K(3), K(4), K(9), K(10)], tried);   // the game's named entries, then the unnamed keys in file order
        Assert.Equal("0x" + K(10), stored.Stored(game)!.KeyString, ignoreCase: true);
        Assert.Contains("checking the key imported for \"Wardogs\"", log);
        Assert.Contains("checking 3 unnamed keys", log);   // one line for them all
        foreach (var text in log.Append(r.Message))
            foreach (var k in Enumerable.Range(0, 30).Select(K))
                Assert.DoesNotContain(k, text, StringComparison.OrdinalIgnoreCase);

        tried.Clear();
        var first = KeyCollection.TryImported(named, unnamed, [game.Name], k => { tried.Add(k); return k == K(4); });
        Assert.Equal((KeyLookupOutcome.Unlocked, "Wardogs (playtest)"), (first.Outcome, first.Entry));
        Assert.Equal([K(3), K(4)], tried);   // a named entry worked: no unnamed key tried

        var other = FakeGame(_dir, "Hotel") with { Id = "test:other" };
        var calls = 0;
        var none = KeyCollection.TryImported(named, [.. Enumerable.Range(0, KeyCollection.MaxUnnamed + 10).Select(Many)], [other.Name], k => { calls++; return stored.Set(other, k, _ => false); });
        Assert.Equal((KeyLookupOutcome.NoWorkingKey, KeyCollection.MaxUnnamed, KeyCollection.MaxUnnamed), (none.Outcome, none.Tried, calls));
        Assert.Equal($"No working key ({KeyCollection.MaxUnnamed} tried).", none.Message);
        Assert.Null(stored.Stored(other));
    }

    [Fact]
    public async Task Imported_entries_survive_a_refresh_of_the_list_and_serve_later_lookups()
    {
        var down = false;
        var server = new Server(() => down ? Html("oops", HttpStatusCode.InternalServerError) : Html(Page));
        var keys = new KeyCollection(_dir, server, _clock);
        await keys.ListAsync(Url, false);
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "mine.txt");
        File.WriteAllText(file, $"Zulu Game 0x{K(50)}\nWardogs 0x{K(51)}\n0x{K(52)}");
        var (got, problem) = keys.ImportFile(file);
        Assert.Equal((1 + 1, 1, (string?)null), (got!.Value.Named.Count, got.Value.Unnamed.Count, problem));
        File.WriteAllText(file, $"Zulu Game 0x{K(53)}");
        keys.ImportFile(file);
        Assert.Equal([new("Zulu Game", K(53)), new("Zulu Game", K(50)), new("Wardogs", K(51))], keys.Imported());   // the latest import first
        Assert.Equal([K(52)], keys.ImportedUnnamed());   // the unnamed keys too

        _clock.Now += TimeSpan.FromHours(25);
        Assert.Equal(9, (await keys.ListAsync(Url, false)).List!.Count);   // refetched: the list as the page has it
        Assert.Equal(2, server.Requests);
        Assert.DoesNotContain(K(50), File.ReadAllText(Path.Combine(_dir, "keys", "collection.json")));
        Assert.Equal(3, keys.Imported().Count);

        var tried = new List<string>();
        var r = await keys.LookUpAsync(Url, ["Wardogs"], k => { tried.Add(k); return k == K(3); }, true);
        Assert.Equal((KeyLookupOutcome.Unlocked, "Wardogs"), (r.Outcome, r.Entry));
        Assert.Equal([K(51), K(52), K(3)], tried);   // the imported entry first, then the unnamed key
        Assert.Contains("listed for", r.Message);
        var zulu = await new KeyCollection(_dir, server, _clock).LookUpAsync(Url, ["Zulu Game"], k => k == K(50), true);
        Assert.Equal(KeyLookupOutcome.Unlocked, zulu.Outcome);
        Assert.Contains("imported for \"Zulu Game\"", zulu.Message);

        down = true;
        var fresh = new KeyCollection(Path.Combine(_dir, "empty"), server, _clock);
        Directory.CreateDirectory(Path.Combine(_dir, "empty", "keys"));
        File.Copy(Path.Combine(_dir, "keys", "imported.json"), Path.Combine(_dir, "empty", "keys", "imported.json"));
        var offline = await fresh.LookUpAsync(Url, ["Zulu Game"], k => k == K(50), true);
        Assert.Equal(KeyLookupOutcome.Unlocked, offline.Outcome);   // no list at all: the imported entries still
        Assert.Contains("from your imported keys", offline.Message);
        Assert.Equal(KeyLookupOutcome.FetchFailed, (await fresh.LookUpAsync(Url, ["Hotel"], k => k != K(52), true)).Outcome);   // the unnamed key, then no list
    }

    [Fact]
    public void An_import_keeps_an_existing_files_entries_and_unnamed_keys_no_named_entry_has()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "keys"));
        var path = Path.Combine(_dir, "keys", "imported.json");
        File.WriteAllText(path, $$"""{"entries":[{"name":"Alpha","key":"{{K(1).ToLowerInvariant()}}"},{"name":"Bravo","key":"{{K(2)}}"}]}""");   // from before unnamed keys were kept
        var keys = new KeyCollection(_dir, new Server(() => Html(Page)), _clock);
        var file = Path.Combine(_dir, "mine.txt");
        File.WriteAllText(file, string.Join("\n", [$"Charlie 0x{K(3)}", $"0x{K(2)}", $"0x{K(4)}", .. Enumerable.Range(0, 600).Select(i => "0x" + Many(i))]));
        Assert.Null(keys.ImportFile(file).Problem);
        Assert.Equal([new("Charlie", K(3)), new("Alpha", K(1)), new("Bravo", K(2))], keys.Imported());
        var unnamed = keys.ImportedUnnamed();
        Assert.Equal((KeyCollection.MaxImportedUnnamed, K(4)), (unnamed.Count, unnamed[0]));
        Assert.DoesNotContain(K(2), unnamed);   // Bravo's

        File.WriteAllText(file, $"0x{K(5)}");
        keys.ImportFile(file);
        Assert.Equal([K(5), K(4)], keys.ImportedUnnamed().Take(2));   // the latest import first
        Assert.Equal(3, keys.Imported().Count);

        File.WriteAllText(path, "{ \"entries\": [ oops");
        var (got, problem) = keys.ImportFile(file);
        Assert.NotNull(got);
        Assert.Contains("can't be read", problem);
        Assert.Equal("{ \"entries\": [ oops", File.ReadAllText(path));   // never replaced: its keys would be lost
    }

    sealed class SyncLog(List<string> lines) : IProgress<string>
    {
        public void Report(string value) { lock (lines) lines.Add(value); }
    }
}
