using System.Net;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Tests.Platform;

// The automatic key lookup after a scan (Settings.LookUpKeysOnline): off by default, then once per game and list.
public partial class AppTests
{
    static readonly EngineInfo EncryptedUnreal = new("Unreal", "5.4", null, "D3D12", true, "encrypted game files (needs the game's AES key)");

    sealed class KeyPage : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent($"<div class=\"postbody\">Fake Game&nbsp;0x{new string('A', 32)}{new string('5', 32)}</div>", Encoding.UTF8, "text/html") });
        }
    }

    [Fact]
    public async Task Key_lookups_are_off_by_default_and_then_run_once_per_game_after_a_scan()
    {
        var page = new KeyPage();
        var reader = new FakeReader(EncryptedUnreal);
        var k = Killer(reader);
        var tries = 0;
        k.KeyList = new KeyCollection(Path.Combine(_root, "data"), page);
        k.KeyCheck = g => ([g.Name], _ => { Interlocked.Increment(ref tries); return false; });
        Assert.False(k.Settings.LookUpKeysOnline);
        await k.ScanAsync(default);
        await k.KeyLookupPass;
        Assert.Equal((0, 0), (page.Requests, tries));   // off: nothing fetched, nothing tried

        k.Settings = k.Settings with { LookUpKeysOnline = true };   // turning it on looks up the listed games
        await k.KeyLookupPass;
        Assert.Equal((1, 1), (page.Requests, tries));
        await k.ScanAsync(default);
        await k.KeyLookupPass;
        Assert.Equal((1, 1), (page.Requests, tries));   // the same candidates for the same exe: not again, and the list is fresh

        k.KeyCheck = g => ([g.Name], _ => { Interlocked.Increment(ref tries); return true; });
        var detects = reader.Detects;
        var r = await k.LookUpKeyAsync(_game.Id);   // the user's button tries again
        Assert.Equal((KeyLookupOutcome.Unlocked, "Fake Game", 2), (r.Outcome, r.Entry, tries));
        Assert.Equal(detects, reader.Detects);   // the caller rescans (as after a pasted key)
    }

    [Fact]
    public async Task A_saved_page_loaded_for_one_game_serves_the_other_encrypted_games()
    {
        var install = Path.Combine(_root, "Other");
        Directory.CreateDirectory(install);
        var other = new Game("test:other", "Other Game", Store.Other, install, Path.Combine(install, "Other.exe"));
        var page = new KeyPage();
        var k = Killer(new FakeReader(EncryptedUnreal), games: [_game, other]);
        var tried = new List<string>();
        k.KeyList = new KeyCollection(Path.Combine(_root, "data"), page);
        k.KeyCheck = g => ([g.Name], key => { lock (tried) tried.Add(g.Name); return false; });
        await k.ScanAsync(default);
        k.Settings = k.Settings with { LookUpKeysOnline = true };
        await k.KeyLookupPass;   // the fetched list has only this game's old key
        var saved = $"<div class=\"postbody\">Intro<br>Other Game 0x{new string('C', 64)}<br>Fake Game 0x{new string('D', 64)}</div>";
        var r = await k.LookUpKeyAsync(_game.Id, saved);
        await k.KeyLookupPass;
        Assert.Equal(KeyLookupOutcome.NoWorkingKey, r.Outcome);
        Assert.Equal(["Fake Game", "Fake Game", "Other Game"], tried.Order());   // the other game from the saved copy
        Assert.Equal(1, page.Requests);   // the saved copy is the fresh list
    }

    sealed class EngineByName(Func<Game, EngineInfo?> engine) : IEngineReader
    {
        public int Detects;
        public EngineInfo? Detect(Game game) { Interlocked.Increment(ref Detects); return engine(game); }
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) => new("content-1", ["PCD3D_SM6"], new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    sealed class Lines(List<string> lines) : IProgress<string>
    {
        public void Report(string value) { lock (lines) lines.Add(value); }
    }

    [Fact]
    public async Task A_key_file_import_unlocks_only_encrypted_games_and_never_logs_a_key()
    {
        Game Other(string name)
        {
            var install = Path.Combine(_root, name);
            Directory.CreateDirectory(install);
            return new("test:" + name.Replace(' ', '-').ToLowerInvariant(), name, Store.Other, install, Path.Combine(install, "Game.exe"));
        }
        var (other, plain, keyed, unity) = (Other("Other Game"), Other("Plain Game"), Other("Keyed Game"), Other("Unity Game"));
        var reader = new EngineByName(g => g == plain || g == keyed ? Unreal : g == unity ? Unreal with { Family = "Unity" } : EncryptedUnreal);
        var k = Killer(reader, games: [_game, other, plain, keyed, unity]);
        var log = new List<string>();
        k.Log = new Lines(log);
        string Key(int i) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([(byte)i]));
        var tried = new List<(string, string)>();
        k.KeyCheck = g => ([g.Name], key => { lock (tried) tried.Add((g.Name, key)); return g == other && key == Key(4); });
        await k.ScanAsync(default);
        var dir = new SCSKiller.Core.App.AppStore(Path.Combine(_root, "data")).GameDir(keyed.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "aes.key"), "0x" + Key(9));
        var file = Path.Combine(_root, "keys.txt");
        File.WriteAllText(file, $"Fake Game 0x{Key(1)}\nFake Game (demo): {Key(2)}\njunk 0x12\n0x{Key(3)}\n{Key(4)}\n0x{Key(5)}");
        var detects = reader.Detects;

        var r = await k.ImportKeysAsync(file);
        Assert.Equal((2, 3, 1, (string?)null), (r.Named, r.Unnamed, r.Unlocked, r.Problem));
        Assert.Equal([("Other Game", KeyImportOutcome.Unlocked, "unnamed key #2"), ("Fake Game", KeyImportOutcome.NoWorkingKey, null),
            ("Keyed Game", KeyImportOutcome.Skipped, null), ("Plain Game", KeyImportOutcome.Skipped, null)], r.Games.Select(g => (g.Name, g.Outcome, g.Entry)));
        Assert.Equal(5, r.Games.Single(g => g.Name == "Fake Game").Tried);
        Assert.Contains("already has a working key", r.Games.Single(g => g.Name == "Keyed Game").Message);
        Assert.Contains("aren't encrypted", r.Games.Single(g => g.Name == "Plain Game").Message);
        Assert.Equal([Key(1), Key(2), Key(3), Key(4), Key(5)], tried.Where(t => t.Item1 == "Fake Game").Select(t => t.Item2));   // named, then unnamed
        Assert.Equal([Key(3), Key(4)], tried.Where(t => t.Item1 == "Other Game").Select(t => t.Item2));
        Assert.Equal(detects, reader.Detects);   // the caller rescans (as after a pasted key)
        foreach (var text in log.Concat(r.Games.Select(g => g.Message)))
            foreach (var key in Enumerable.Range(1, 9).Select(Key))
                Assert.DoesNotContain(key, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(log, l => l.Contains("unnamed key #1"));
        Assert.Equal(["Fake Game", "Fake Game (demo)"], k.KeyList.Imported().Select(e => e.Name));   // kept for later lookups

        var missing = await k.ImportKeysAsync(Path.Combine(_root, "none.txt"));
        Assert.Equal((0, 0), (missing.Games.Count, missing.Named));
        Assert.NotNull(missing.Problem);
    }

    static string Hex(int i) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([(byte)i]));
    static readonly string PageKey = new string('A', 32) + new string('5', 32);   // KeyPage's

    [Fact]
    public async Task Imported_keys_are_tried_after_every_scan_without_the_network_once_per_import()
    {
        var page = new KeyPage();
        var tried = new List<(string Game, string Key)>();
        Func<Game, (string[], Func<string, bool>)?> check = g => ([g.Name], key => { lock (tried) tried.Add((g.Name, key)); return g.Name == "Other Game" && key == Hex(7); });
        var k = Killer(new FakeReader(EncryptedUnreal));
        k.KeyList = new KeyCollection(Path.Combine(_root, "data"), page);
        k.KeyCheck = check;
        Assert.False(k.Settings.LookUpKeysOnline);
        await k.ScanAsync(default);
        var file = Path.Combine(_root, "keys.txt");
        File.WriteAllText(file, $"Other Game 0x{Hex(7)}\nFake Game 0x{Hex(8)}");
        var r = await k.ImportKeysAsync(file);
        Assert.Equal(KeyImportOutcome.NoWorkingKey, r.Games.Single().Outcome);
        Assert.Equal([("Fake Game", Hex(8))], tried);

        await k.ScanAsync(default);
        await k.KeyLookupPass;
        Assert.Single(tried);   // the import tried them: not again for the same exe

        var install = Path.Combine(_root, "Other");   // installed after the import
        Directory.CreateDirectory(install);
        var other = new Game("test:other", "Other Game", Store.Other, install, Path.Combine(install, "Other.exe"));
        var reader = new FakeReader(EncryptedUnreal);
        var k2 = Killer(reader, games: [_game, other]);
        k2.KeyList = new KeyCollection(Path.Combine(_root, "data"), page);
        k2.KeyCheck = check;
        await k2.ScanAsync(default);
        var changed = new List<string>();
        k2.GameChanged += s => { lock (changed) changed.Add(s.Game.Id); };
        await k2.KeyLookupPass;
        Assert.Equal([("Fake Game", Hex(8)), ("Other Game", Hex(7))], tried);   // the next scan unlocks it
        Assert.Equal([other.Id], changed);   // and re-evaluates it

        File.WriteAllText(file, $"Fake Game 0x{Hex(9)}");
        k2.KeyList.ImportFile(file);   // a new import: retried at the next scan
        await k2.ScanAsync(default);
        await k2.KeyLookupPass;
        Assert.Equal([Hex(8), Hex(9), Hex(8)], tried.Where(t => t.Game == "Fake Game").Select(t => t.Key));
        await k2.ScanAsync(default);
        await k2.KeyLookupPass;
        Assert.Equal(4, tried.Count);   // unchanged: not again
        Assert.Equal(0, page.Requests);   // the online lookup is off: nothing fetched
    }

    [Fact]
    public async Task A_lookup_tries_imported_keys_before_the_online_list_and_each_key_once()
    {
        var page = new KeyPage();
        var tried = new List<string>();
        var works = "";
        var k = Killer(new FakeReader(EncryptedUnreal));
        k.KeyList = new KeyCollection(Path.Combine(_root, "data"), page);
        k.KeyCheck = g => ([g.Name], key => { lock (tried) tried.Add(key); return key == works; });
        var file = Path.Combine(_root, "keys.txt");
        File.WriteAllText(file, $"Fake Game 0x{Hex(1)}\nFake Game (old) 0x{PageKey}");
        k.KeyList.ImportFile(file);
        k.Settings = k.Settings with { LookUpKeysOnline = true };
        await k.ScanAsync(default);
        await k.KeyLookupPass;
        Assert.Equal([Hex(1), PageKey], tried);   // the listed key is one of the imported: not tried twice
        Assert.Equal(1, page.Requests);   // nothing imported worked: the list

        works = Hex(1);
        tried.Clear();
        var r = await k.LookUpKeyAsync(_game.Id);
        Assert.Equal((KeyLookupOutcome.Unlocked, "Fake Game", 1), (r.Outcome, r.Entry, r.Tried));
        Assert.Contains("from your imported keys", r.Message);
        Assert.Equal(1, page.Requests);   // found locally: not fetched

        File.WriteAllText(file, $"Zulu 0x{Hex(2)}");
        new KeyCollection(Path.Combine(_root, "other-data")).ImportFile(file);
        k.KeyList = new KeyCollection(Path.Combine(_root, "other-data"), page);
        works = PageKey;
        r = await k.LookUpKeyAsync(_game.Id);
        Assert.Equal(KeyLookupOutcome.Unlocked, r.Outcome);
        Assert.Contains("from the online list", r.Message);
    }

    [Fact]
    public async Task A_scan_never_looks_up_keys_of_unencrypted_games()
    {
        var page = new KeyPage();
        var k = Killer(new FakeReader(Unreal));
        k.KeyList = new KeyCollection(Path.Combine(_root, "data"), page);
        k.KeyCheck = g => throw new InvalidOperationException("not looked up");
        k.Settings = k.Settings with { LookUpKeysOnline = true };
        await k.ScanAsync(default);
        await k.KeyLookupPass;
        Assert.Equal(0, page.Requests);
    }
}
