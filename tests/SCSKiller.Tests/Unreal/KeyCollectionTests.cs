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
        Assert.Contains("SCSKiller/", server.Last!.Headers.UserAgent.ToString());
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

    sealed class SyncLog(List<string> lines) : IProgress<string>
    {
        public void Report(string value) { lock (lines) lines.Add(value); }
    }
}
