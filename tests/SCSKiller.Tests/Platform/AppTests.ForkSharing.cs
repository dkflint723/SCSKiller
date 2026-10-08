using System.Net;
using System.Text.Json;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.SquareEnix;

namespace SCSKiller.Tests.Platform;

// This unofficial build's upload safeguards: its own opt-in (fork.json) besides Settings.ShareRecordings, and only
// recordings an official build could have made.
public partial class AppTests
{
    const string ForkHash = "00112233445566778899aabbccddeeff00112233";

    sealed class ForkLog(List<string> lines) : IProgress<string> { public void Report(string value) { lock (lines) lines.Add(value); } }

    /// <summary>A killer whose game holds a recording of build 42 indexed with <see cref="ForkHash"/>, both opt-ins on unless
    /// <paramref name="forkOn"/> is false, and a fake server counting uploads.</summary>
    async Task<(ScsKiller K, Func<int> Uploads, List<string> Log)> ForkSharer(EngineInfo? engine = null, bool forkOn = true)
    {
        var game = _game with { Version = "42" };
        File.WriteAllBytes(Path.Combine(_exeDir, "scskiller.db"), SharingTests.LocalRecording());
        var fake = new CommunityTests.Fake(r => r.RequestUri!.AbsolutePath == "/v1/devices"
            ? CommunityTests.Ours(HttpStatusCode.OK, """{"device_token":"sd1_anon","device_id":"d"}"""u8.ToArray())
            : CommunityTests.Ours(HttpStatusCode.Accepted, """{"upload_id":"u","records":1,"new_records":1}"""u8.ToArray()));
        var log = new List<string>();
        var k = Killer(new FakeReader(engine ?? Unreal, ForkHash), game: game);
        k.Log = new ForkLog(log);
        await k.ScanAsync(default);   // imports the recording; sharing is off
        var rec = k.Store.LoadGame(game.Id);
        (rec.IndexContentHash, rec.IndexGameVersion) = (ForkHash, "42");
        k.Store.SaveGame(game.Id, rec);
        k.Settings = k.Settings with { ShareRecordings = true };
        if (forkOn) k.ForkSettings = new(ShareRecordings: true);
        k.Sharing = new Sharing(Path.Combine(_root, "data"), () => k.SharesRecordings, new RouteFailover(fake, [new("https://api.test.com/"), new("https://api.test.io/")]));
        return (k, () => { lock (fake.Log) return fake.Log.Count(l => l.Contains("/v1/upload")); }, log);
    }

    /// <summary>The engine an earlier run's scan read (scan.json): a pass before this run's first scan shares only an
    /// official reader's index (<see cref="ForkBuild.UploadBlock"/>).</summary>
    static void SeedScan(AppStore store, Game game) =>
        store.SaveScan(new() { [game.Id] = new("an earlier run", Unreal, AntiCheat.None, new(Readiness.Ready, "")) });

    [Fact]
    public async Task Before_any_scan_a_game_whose_engine_isnt_known_is_not_shared()
    {
        var game = _game with { Version = "42" };
        var store = new AppStore(Path.Combine(_root, "data"));
        Directory.CreateDirectory(store.GameDir(game.Id));
        File.WriteAllBytes(Path.Combine(store.GameDir(game.Id), "recording.db"), SharingTests.LocalRecording());
        var rec = store.LoadGame(game.Id);
        (rec.IndexContentHash, rec.IndexGameVersion) = (ForkHash, "42");
        store.SaveGame(game.Id, rec);
        var fake = new CommunityTests.Fake(r => r.RequestUri!.AbsolutePath == "/v1/devices"
            ? CommunityTests.Ours(HttpStatusCode.OK, """{"device_token":"sd1_anon","device_id":"d"}"""u8.ToArray())
            : CommunityTests.Ours(HttpStatusCode.Accepted, """{"upload_id":"u","records":1,"new_records":1}"""u8.ToArray()));
        var k = Killer(new FakeReader(Unreal, ForkHash), game: game);
        k.Settings = k.Settings with { ShareRecordings = true };
        k.ForkSettings = new(ShareRecordings: true);
        k.Sharing = new Sharing(store.DataDir, () => k.SharesRecordings, new RouteFailover(fake, [new("https://api.test.com/")]));
        k.StartSharing([game]);
        await k.SharingPass;
        Assert.Empty(fake.Log);   // no upload, no device, no pack either: the games aren't read yet

        SeedScan(store, game);
        k.StartSharing([game]);
        await k.SharingPass;
        Assert.Single(fake.Log, l => l.Contains("/v1/upload"));
    }

    async Task Share(ScsKiller k)
    {
        k.StartSharing([k.Games.Single().Game]);
        await k.SharingPass;
    }

    [Fact]
    public async Task Uploads_need_this_builds_own_opt_in_besides_the_setting()
    {
        var (k, uploads, _) = await ForkSharer(forkOn: false);
        Assert.False(k.ForkSettings.ShareRecordings);   // off until the user ticks it
        Assert.False(k.SharesRecordings);
        await Share(k);
        Assert.Equal(0, uploads());
        Assert.False(File.Exists(Path.Combine(_root, "data", ForkBuild.UploadDevice)));   // no device registered either

        k.ForkSettings = new(ShareRecordings: true);   // turned on: a pass at once
        await k.SharingPass;
        Assert.Equal(1, uploads());
        // kept in fork.json, never in settings.json, which official builds share and rewrite
        Assert.True(new AppStore(Path.Combine(_root, "data")).LoadFork().ShareRecordings);
        Assert.DoesNotContain("Fork", File.ReadAllText(Path.Combine(_root, "data", "settings.json")));

        k.Settings = k.Settings with { ShareRecordings = false };   // the shared setting off: nothing either
        Assert.False(k.SharesRecordings);
    }

    [Fact]
    public void Fork_json_defaults_to_no_sharing_and_a_damaged_one_too()
    {
        var store = new AppStore(Path.Combine(_root, "fork-data"));
        Assert.False(store.LoadFork().ShareRecordings);
        store.SaveFork(new(ShareRecordings: true));
        Assert.True(store.LoadFork().ShareRecordings);
        File.WriteAllText(Path.Combine(store.DataDir, ForkBuild.SettingsFile), "{ not json");
        Assert.False(store.LoadFork().ShareRecordings);
        Assert.False(File.Exists(Path.Combine(store.DataDir, "settings.json")));
    }

    [Fact]
    public async Task A_recording_with_a_launch_without_NVAPI_state_is_not_shared_until_it_is_cleared()
    {
        File.WriteAllText(Path.Combine(_exeDir, "scskiller.ini"), "[scskiller]\r\nframes=0\r\nnvapi=0\r\n");   // the recorder ran at Minimal
        var (k, uploads, log) = await ForkSharer();
        var marker = Path.Combine(k.Store.GameDir(_game.Id), ForkBuild.MinimalMarker);
        Assert.True(File.Exists(marker));   // noted at the import
        File.Delete(Path.Combine(_exeDir, "scskiller.ini"));   // the level back to Full: the recording still holds that launch
        await Share(k);
        Assert.Equal(0, uploads());
        lock (log) Assert.Contains(log, l => l.Contains("not shared from this unofficial build") && l.Contains("NVAPI"));

        Assert.True(k.ClearRecording(_game.Id));
        Assert.False(File.Exists(marker));
        File.WriteAllBytes(Path.Combine(_exeDir, "scskiller.db"), SharingTests.LocalRecording(new string('c', 40)));   // a Full launch
        await k.ScanAsync(default);
        await k.SharingPass;
        await Share(k);
        Assert.Equal(1, uploads());
    }

    [Fact]
    public async Task A_crash_guard_step_down_holds_the_recording_back()
    {
        var (k, uploads, _) = await ForkSharer();
        var rec = k.Store.LoadGame(_game.Id);
        rec.RecorderLevel = RecorderLevel.Minimal;
        k.Store.SaveGame(_game.Id, rec);
        await Share(k);
        Assert.Equal(0, uploads());
    }

    [Fact]
    public async Task A_game_recorded_alongside_a_mod_or_under_ReShade_a_copy_cant_reproduce_is_not_shared()
    {
        var (k, uploads, _) = await ForkSharer();
        var rec = k.Store.LoadGame(_game.Id);
        rec.RecordAlongsideMod = true;
        k.Store.SaveGame(_game.Id, rec);
        await Share(k);
        Assert.Equal(0, uploads());

        rec.RecordAlongsideMod = false;
        k.Store.SaveGame(_game.Id, rec);
        var reshade = Path.Combine(_exeDir, "ReShade64.dll");   // a name the game doesn't load by itself: above the recorder, if at all
        File.WriteAllBytes(reshade, ReShadeDll);
        await Share(k);
        Assert.Equal(0, uploads());

        File.Delete(reshade);
        await Share(k);
        Assert.Equal(1, uploads());
    }

    [Fact]
    public async Task A_game_read_by_a_reader_only_this_build_has_is_not_shared()
    {
        var (k, uploads, log) = await ForkSharer(new EngineInfo(PspcReader.Family, "0x0300000A", null, "D3D12", false, null, ShipsRootSignatures: true));
        await Share(k);
        Assert.Equal(0, uploads());
        lock (log) Assert.Single(log, l => l.Contains("a reader only this unofficial build has (Square Enix PSPC)"));
        await Share(k);
        lock (log) Assert.Single(log, l => l.Contains("a reader only this unofficial build has"));   // once a run
    }

    [Theory]
    [InlineData("Unreal", false, null)]
    [InlineData("RE Engine", false, null)]
    [InlineData("Unity", false, null)]
    [InlineData("Square Enix PSPC", false, "reader only this unofficial build has")]
    [InlineData("Unreal", true, "reader only this unofficial build has")]   // root signatures from the game's files: never upstream's
    [InlineData("Some Engine", false, "reader only this unofficial build has")]
    public void Only_an_official_readers_index_is_shared(string family, bool shipsRootSignatures, string? why)
    {
        var block = ForkBuild.UploadBlock(new(family, "1", null, "D3D12", false, null, ShipsRootSignatures: shipsRootSignatures), false, false, null);
        if (why == null) Assert.Null(block);
        else Assert.Contains(why, block);
        Assert.NotNull(ForkBuild.UploadBlock(null, false, false, null));   // not read yet: not shared
    }

    [Fact]
    public void What_a_recording_holds_blocks_it_whatever_read_the_game()
    {
        var beside = new ReShadeInstall(Path.Combine(_exeDir, "dxgi.dll"), true, null, null, []);
        var unloaded = beside with { Dll = Path.Combine(_exeDir, "ReShade64.dll") };
        Assert.True(beside.Copyable);
        Assert.False(unloaded.Copyable);
        Assert.Null(ForkBuild.ContentBlock(false, false, null));
        Assert.Null(ForkBuild.ContentBlock(false, false, beside));   // a copy reproduces it: its changes are 'W'-flagged or layered
        Assert.Contains("NVAPI", ForkBuild.ContentBlock(true, false, null));
        Assert.Contains("alongside a mod", ForkBuild.ContentBlock(false, true, null));
        Assert.Contains("ReShade", ForkBuild.ContentBlock(false, false, unloaded));
    }

    [Theory]
    [InlineData("[scskiller]\r\nframes=0\r\nnvapi=0\r\n", null, true)]
    [InlineData("[scskiller]\r\n  NVAPI = 0 \r\n", null, true)]
    [InlineData("[scskiller]\r\n; nvapi=0 is what the crash guard writes\r\nnvapi=1\r\n", null, false)]
    [InlineData("[scskiller]\r\nnvapi=01\r\n", null, false)]
    [InlineData(null, null, false)]
    [InlineData(null, "0", true)]
    [InlineData("nvapi=0", "1", true)]
    public void NVAPI_off_is_read_from_the_ini_or_the_environment(string? ini, string? environment, bool off) =>
        Assert.Equal(off, ForkBuild.NvapiOff(ini, environment));

    [Fact]
    public void Fork_json_is_its_own_file_and_names_no_settings_field()
    {
        var json = JsonSerializer.Serialize(new ForkSettings(), AppStore.Json);
        Assert.Contains("ShareRecordings", json);
        Assert.Equal(["ShareRecordings"], JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name));
        Assert.NotEqual("settings.json", ForkBuild.SettingsFile);
        Assert.NotEqual("upload.dat", ForkBuild.UploadDevice);
    }
}
