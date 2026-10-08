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
        k.KeyCheck = g => new([g.Name], _ => { Interlocked.Increment(ref tries); return false; });
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

        k.KeyCheck = g => new([g.Name], _ => { Interlocked.Increment(ref tries); return true; });
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
        k.KeyCheck = g => new([g.Name], key => { lock (tried) tried.Add(g.Name); return false; });
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

    /// <summary>An encrypted Unreal game until a key is stored for it (<see cref="Unlock"/>), as UnrealReader, whose detect
    /// stamp has the key file's: a key stored or lost re-detects the game.</summary>
    sealed class Keyed : IEngineReader
    {
        readonly HashSet<string> unlocked = [];
        public void Unlock(Game g) { lock (unlocked) unlocked.Add(g.Id); }
        public void Lose(Game g) { lock (unlocked) unlocked.Remove(g.Id); }
        bool Has(Game g) { lock (unlocked) return unlocked.Contains(g.Id); }
        public EngineInfo? Detect(Game game) => Has(game) ? Unreal : EncryptedUnreal;
        public string DetectStamp(Game game, EngineInfo? engine) => Has(game) ? "key" : "";
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) => new("content-1", ["PCD3D_SM6"], new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
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
        k.KeyCheck = g => new([g.Name], key => { lock (tried) tried.Add((g.Name, key)); return g == other && key == Key(4); });
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
        Assert.Contains(log, l => l.Contains("checking 3 unnamed keys"));
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
        var reader = new Keyed();
        Func<Game, KeyTrial?> check = g => new([g.Name], key =>
        {
            lock (tried) tried.Add((g.Name, key));
            if (g.Name != "Other Game" || key != Hex(7)) return false;
            reader.Unlock(g);
            return true;
        });
        var k = Killer(reader);
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

    string GameDir(Game g) => new SCSKiller.Core.App.AppStore(Path.Combine(_root, "data")).GameDir(g.Id);

    [Fact]
    public async Task A_key_found_and_lost_is_looked_up_again_but_candidates_that_failed_are_not()
    {
        var page = new KeyPage();
        var reader = new Keyed();
        var k = Killer(reader);
        k.KeyList = new KeyCollection(Path.Combine(_root, "data"), page);
        var (tries, opens) = (0, true);
        k.KeyCheck = g => new([g.Name], key =>
        {
            Interlocked.Increment(ref tries);
            if (key != Hex(1) || !opens) return false;
            reader.Unlock(g);
            return true;
        });
        await k.ScanAsync(default);
        var file = Path.Combine(_root, "keys.txt");
        File.WriteAllText(file, $"Fake Game 0x{Hex(1)}");
        Assert.Equal(1, (await k.ImportKeysAsync(file)).Unlocked);
        var memo = Path.Combine(GameDir(_game), "aes.lookup");
        Assert.False(File.Exists(memo));   // only candidates tried in vain are remembered
        await k.ScanAsync(default);
        await k.KeyLookupPass;
        Assert.Equal(1, tries);   // unlocked: not looked up

        reader.Lose(_game);   // the key file deleted
        await k.ScanAsync(default);
        await k.KeyLookupPass;
        Assert.Equal((2, 0), (tries, page.Requests));   // unlocked again from the imported keys, nothing fetched
        Assert.False(k.Games.Single().Engine!.Encrypted);   // and re-evaluated

        opens = false;   // the key stops opening the files, the same exe
        reader.Lose(_game);
        await k.ScanAsync(default);
        await k.KeyLookupPass;
        Assert.Equal(3, tries);   // tried again
        await k.ScanAsync(default);
        await k.KeyLookupPass;
        Assert.Equal(3, tries);   // in vain: not again for the same exe and keys
        Assert.True(File.Exists(memo));

        // a memo from before only failures were remembered, with a key stored that doesn't open the files: tried again once
        File.WriteAllText(memo, string.Join('\n', File.ReadAllText(memo).Split('\n').Take(2)));
        var legacy = File.ReadAllText(memo);
        await k.ScanAsync(default);
        await k.KeyLookupPass;
        Assert.Equal(3, tries);   // without a stored key it may be a failure's: kept
        File.WriteAllText(Path.Combine(GameDir(_game), "aes.key"), "0x" + Hex(1));
        await k.ScanAsync(default);
        await k.KeyLookupPass;
        Assert.Equal(4, tries);
        await k.ScanAsync(default);
        await k.KeyLookupPass;
        Assert.Equal(4, tries);
        Assert.StartsWith(legacy, File.ReadAllText(memo));
    }

    [Fact]
    public async Task Unnamed_imported_keys_are_kept_and_unlock_a_game_installed_after_the_import()
    {
        var reader = new Keyed();
        var tried = new List<(string Game, string Key)>();
        Func<Game, KeyTrial?> check = g => new([g.Name], key =>
        {
            lock (tried) tried.Add((g.Name, key));
            if (g.Name != "Other Game" || key != Hex(5)) return false;
            reader.Unlock(g);
            return true;
        });
        var k = Killer(reader);
        k.KeyList = new KeyCollection(Path.Combine(_root, "data"), new KeyPage());
        k.KeyCheck = check;
        await k.ScanAsync(default);
        var file = Path.Combine(_root, "keys.txt");
        File.WriteAllText(file, $"0x{Hex(5)}\nFake Game 0x{Hex(6)}");
        var r = await k.ImportKeysAsync(file);
        Assert.Equal((1, 1, KeyImportOutcome.NoWorkingKey), (r.Named, r.Unnamed, r.Games.Single().Outcome));
        Assert.Equal([("Fake Game", Hex(6)), ("Fake Game", Hex(5))], tried);
        Assert.Equal([Hex(5)], k.KeyList.ImportedUnnamed());   // kept in keys\imported.json
        Assert.Contains("\"unnamed\"", File.ReadAllText(Path.Combine(_root, "data", "keys", "imported.json")));

        var install = Path.Combine(_root, "Other");   // installed after the import
        Directory.CreateDirectory(install);
        var other = new Game("test:other", "Other Game", Store.Other, install, Path.Combine(install, "Other.exe"));
        var k2 = Killer(reader, games: [_game, other]);
        k2.KeyList = new KeyCollection(Path.Combine(_root, "data"), new KeyPage());
        k2.KeyCheck = check;
        await k2.ScanAsync(default);
        await k2.KeyLookupPass;
        Assert.Equal([("Fake Game", Hex(6)), ("Fake Game", Hex(5)), ("Other Game", Hex(5))], tried);   // the unnamed key, after its named ones (none)
        Assert.False(k2.Games.Single(s => s.Game.Id == other.Id).Engine!.Encrypted);
    }

    [Fact]
    public async Task The_user_lookup_reports_its_stages_in_order()
    {
        var page = new KeyPage();
        var k = Killer(new FakeReader(EncryptedUnreal));
        k.KeyList = new KeyCollection(Path.Combine(_root, "data"), page);
        k.KeyCheck = g => new([g.Name], _ => false);
        var file = Path.Combine(_root, "keys.txt");
        File.WriteAllText(file, $"Fake Game 0x{Hex(1)}");
        k.KeyList.ImportFile(file);
        await k.ScanAsync(default);
        await k.KeyLookupPass;
        var stages = new List<string>();
        var r = await k.LookUpKeyAsync(_game.Id, stage: new Lines(stages));
        Assert.Equal(KeyLookupOutcome.NoWorkingKey, r.Outcome);
        Assert.Equal(["Trying your imported keys…", "Checking the community's key list…"], stages);
        stages.Clear();
        await k.LookUpKeyAsync(_game.Id, $"<div class=\"postbody\">Fake Game 0x{Hex(2)}</div>", new Lines(stages));
        Assert.Equal(["Trying your imported keys…", "Checking the saved page…"], stages);
    }

    [Fact]
    public async Task An_import_waits_for_the_key_lookup_of_a_scan()
    {
        var k = Killer(new FakeReader(EncryptedUnreal));
        k.KeyList = new KeyCollection(Path.Combine(_root, "data"), new KeyPage());
        var file = Path.Combine(_root, "keys.txt");
        File.WriteAllText(file, $"Fake Game 0x{Hex(1)}");
        k.KeyList.ImportFile(file);
        using var go = new ManualResetEventSlim();
        using var entered = new SemaphoreSlim(0);
        var (inside, most) = (0, 0);
        k.KeyCheck = g => new([g.Name], _ =>
        {
            var n = Interlocked.Increment(ref inside);
            lock (entered) most = Math.Max(most, n);
            entered.Release();
            go.Wait();
            Interlocked.Decrement(ref inside);
            return false;
        });
        await k.ScanAsync(default);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(30)));   // the scan's pass is trying the imported key
        File.WriteAllText(file, $"Fake Game 0x{Hex(2)}");
        var progress = new List<string>();
        var import = k.ImportKeysAsync(file, new Lines(progress));
        await Task.Delay(300);
        Assert.False(import.IsCompleted);
        Assert.Equal(0, entered.CurrentCount);   // nothing tried by the import meanwhile
        Assert.Equal(["Waiting for the key lookup that runs after a scan…"], progress);
        go.Set();
        var r = await import;
        Assert.Equal(2, r.Games.Single().Tried);
        Assert.Equal(1, most);
        await k.KeyLookupPass;
    }

    [Fact]
    public async Task An_import_says_when_its_keys_could_not_be_kept()
    {
        var k = Killer(new FakeReader(EncryptedUnreal));
        k.KeyList = new KeyCollection(Path.Combine(_root, "data"), new KeyPage());
        var tries = 0;
        k.KeyCheck = g => new([g.Name], _ => { Interlocked.Increment(ref tries); return false; });
        await k.ScanAsync(default);
        var file = Path.Combine(_root, "keys.txt");
        File.WriteAllText(file, $"Fake Game 0x{Hex(1)}");
        var saved = Path.Combine(_root, "data", "keys", "imported.json");
        Directory.CreateDirectory(saved);   // can't be replaced
        var r = await k.ImportKeysAsync(file);
        Assert.Null(r.Problem);
        Assert.Contains("saving", r.NotKept);
        Assert.Equal((KeyImportOutcome.NoWorkingKey, 1), (r.Games.Single().Outcome, tries));   // the games are tried all the same

        Directory.Delete(saved);
        File.WriteAllText(saved, "not json");
        r = await k.ImportKeysAsync(file);
        Assert.Contains("can't be read", r.NotKept);
        Assert.Equal("not json", File.ReadAllText(saved));
        File.Delete(saved);
        Assert.Null((await k.ImportKeysAsync(file)).NotKept);
    }

    [Fact]
    public async Task A_lookup_tries_imported_keys_before_the_online_list_and_each_key_once()
    {
        var page = new KeyPage();
        var tried = new List<string>();
        var works = "";
        var k = Killer(new FakeReader(EncryptedUnreal));
        k.KeyList = new KeyCollection(Path.Combine(_root, "data"), page);
        k.KeyCheck = g => new([g.Name], key => { lock (tried) tried.Add(key); return key == works; });
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
