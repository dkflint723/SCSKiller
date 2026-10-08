using System.Net;
using System.Text.Json;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.SquareEnix;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Tests.Platform;

// This unofficial build's upload safeguards: its own opt-in (fork.json) besides Settings.ShareRecordings, and only
// recordings an official build could have made.
public partial class AppTests
{
    const string ForkHash = "00112233445566778899aabbccddeeff00112233";

    sealed class ForkLog(List<string> lines) : IProgress<string> { public void Report(string value) { lock (lines) lines.Add(value); } }

    /// <summary>A killer whose game holds a recording of build 42 indexed with <see cref="ForkHash"/>, both opt-ins on unless
    /// <paramref name="forkOn"/> is false, its recordings from before the markers looked at unless <paramref name="marked"/>
    /// is false, and a fake server counting uploads.</summary>
    async Task<(ScsKiller K, Func<int> Uploads, List<string> Log)> ForkSharer(EngineInfo? engine = null, bool forkOn = true, IPlanner? planner = null,
        bool marked = true, IGpuVendorBackend? vendor = null)
    {
        var game = _game with { Version = "42" };
        File.WriteAllBytes(Path.Combine(_exeDir, "scskiller.db"), SharingTests.LocalRecording());
        var fake = new CommunityTests.Fake(r => r.RequestUri!.AbsolutePath == "/v1/devices"
            ? CommunityTests.Ours(HttpStatusCode.OK, """{"device_token":"sd1_anon","device_id":"d"}"""u8.ToArray())
            : CommunityTests.Ours(HttpStatusCode.Accepted, """{"upload_id":"u","records":1,"new_records":1}"""u8.ToArray()));
        var log = new List<string>();
        var k = Killer(new FakeReader(engine ?? Unreal, ForkHash), planner, game: game, vendor: vendor);
        k.Log = new ForkLog(log);
        await k.ScanAsync(default);   // imports the recording; sharing is off
        var rec = k.Store.LoadGame(game.Id);
        (rec.IndexContentHash, rec.IndexGameVersion) = (ForkHash, "42");
        k.Store.SaveGame(game.Id, rec);
        k.Settings = k.Settings with { ShareRecordings = true };
        k.ForkSettings = new(ShareRecordings: forkOn, OldRecordingsMarked: marked);
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

        k.ForkSettings = k.ForkSettings with { ShareRecordings = true };   // turned on: a pass at once
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
        lock (log) Assert.Contains(log, l => l.Contains("not shared from this unofficial build") && l.Contains("NVAPI") && l.Contains("clear the recording"));

        Assert.True(k.ClearRecording(_game.Id));
        Assert.False(File.Exists(marker));
        File.WriteAllBytes(Path.Combine(_exeDir, "scskiller.db"), SharingTests.LocalRecording(new string('c', 40)));   // a Full launch
        await k.ScanAsync(default);
        await k.SharingPass;
        await Share(k);
        Assert.Equal(1, uploads());
    }

    [Fact]
    public async Task A_crash_guard_step_down_holds_the_recording_back_while_it_lasts()
    {
        var (k, uploads, log) = await ForkSharer();
        var rec = k.Store.LoadGame(_game.Id);
        rec.RecorderLevel = RecorderLevel.Minimal;
        k.Store.SaveGame(_game.Id, rec);
        await Share(k);
        Assert.Equal(0, uploads());
        // its recording holds no such launch: no clear helps, the level does
        lock (log) Assert.Contains(log, l => l.Contains("runs without NVAPI hooks") && !l.Contains("clear the recording"));
        Assert.False(File.Exists(Path.Combine(k.Store.GameDir(_game.Id), ForkBuild.MinimalMarker)));
        rec.RecorderLevel = null;   // Try again
        k.Store.SaveGame(_game.Id, rec);
        await Share(k);
        Assert.Equal(1, uploads());
    }

    [Fact]
    public async Task A_recording_made_under_ReShade_a_copy_cant_reproduce_is_held_back_until_it_is_cleared_not_until_ReShade_goes()
    {
        var reshade = Path.Combine(_exeDir, "ReShade64.dll");   // a name the game doesn't load by itself: above the recorder, if at all
        File.WriteAllBytes(reshade, ReShadeDll);
        var (k, uploads, log) = await ForkSharer();   // imported under it
        var marker = Path.Combine(k.Store.GameDir(_game.Id), ForkBuild.LayeredMarker);
        Assert.True(File.Exists(marker));
        await Share(k);
        Assert.Equal(0, uploads());

        File.Delete(reshade);   // the recording still holds what it changed
        await Share(k);
        Assert.Equal(0, uploads());
        lock (log) Assert.Contains(log, l => l.Contains("under ReShade") && l.Contains("clear the recording"));

        Assert.True(k.ClearRecording(_game.Id));
        Assert.False(File.Exists(marker));
        File.WriteAllBytes(Path.Combine(_exeDir, "scskiller.db"), SharingTests.LocalRecording(new string('c', 40)));
        await k.ScanAsync(default);
        await k.SharingPass;
        await Share(k);
        Assert.Equal(1, uploads());
    }

    [Fact]
    public async Task ReShade_a_copy_cant_reproduce_put_in_after_the_recording_holds_it_back_only_while_it_is_there()
    {
        var (k, uploads, log) = await ForkSharer();
        var reshade = Path.Combine(_exeDir, "ReShade64.dll");
        File.WriteAllBytes(reshade, ReShadeDll);
        await Share(k);
        Assert.Equal(0, uploads());
        lock (log) Assert.Contains(log, l => l.Contains("ReShade") && l.Contains("while it is") && !l.Contains("clear the recording"));
        Assert.False(File.Exists(Path.Combine(k.Store.GameDir(_game.Id), ForkBuild.LayeredMarker)));   // nothing recorded under it
        File.Delete(reshade);
        await Share(k);
        Assert.Equal(1, uploads());
    }

    [Fact]
    public async Task The_choice_to_record_alongside_a_mod_holds_nothing_back_a_chained_mod_does()
    {
        var (k, uploads, log) = await ForkSharer();
        var rec = k.Store.LoadGame(_game.Id);
        rec.RecorderChained = new ChainedDll("d3d12.scskiller-next.dll", new string('0', 64));   // the recorder runs chained to a mod now
        k.Store.SaveGame(_game.Id, rec);
        await Share(k);
        Assert.Equal(0, uploads());
        lock (log) Assert.Contains(log, l => l.Contains("runs alongside a mod's d3d12.dll"));

        (rec.RecorderChained, rec.RecordAlongsideMod) = (null, true);   // the choice alone, no mod there: the recorder runs as without it
        k.Store.SaveGame(_game.Id, rec);
        await Share(k);
        Assert.Equal(1, uploads());
    }

    [Fact]
    public async Task An_inbox_recorded_chained_to_a_mod_marks_the_recording_when_the_recorder_is_removed()
    {
        var (k, uploads, _) = await ForkSharer();
        await Share(k);
        Assert.Equal(1, uploads());
        // the recorder ran chained (the record says so) and wrote an inbox no import read before its removal
        var rec = k.Store.LoadGame(_game.Id);
        rec.RecorderChained = new ChainedDll("d3d12.scskiller-next.dll", new string('0', 64));
        k.Store.SaveGame(_game.Id, rec);
        File.WriteAllBytes(Path.Combine(_exeDir, "scskiller.db"), SharingTests.LocalRecording(new string('c', 40)));
        k.UninstallRecorder(_game.Id);
        Assert.True(File.Exists(Path.Combine(k.Store.GameDir(_game.Id), ForkBuild.LayeredMarker)));
        Assert.Null(k.Store.LoadGame(_game.Id).RecorderChained);   // gone with the recorder: the marker holds it back
    }

    /// <summary>The recorder's removal deletes its ini before the inbox is merged: what the ini said is read first.</summary>
    [Fact]
    public async Task An_inbox_recorded_without_NVAPI_hooks_marks_the_recording_though_the_removal_takes_the_ini_first()
    {
        var (k, _, _, _) = Guarded(new FakeReader(Pspc));
        foreach (var f in new[] { "sl.interposer.dll", "sl.dlss_g.dll" }) File.WriteAllBytes(Path.Combine(_exeDir, f), Planning.MiddlewarePackTests.Pe(f));
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);   // beside frame generation, where it isn't needed: pipelines only
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        Assert.Contains("\r\nnvapi=0\r\n", File.ReadAllText(ini));
        File.WriteAllBytes(Path.Combine(_exeDir, "scskiller.db"), SharingTests.LocalRecording());   // a launch no import read yet
        k.UninstallRecorder(_game.Id);
        Assert.False(File.Exists(ini));
        Assert.True(File.Exists(Path.Combine(k.Store.GameDir(_game.Id), "recording.db")));
        Assert.True(File.Exists(Path.Combine(k.Store.GameDir(_game.Id), ForkBuild.MinimalMarker)));
    }

    /// <summary>An offline session's cleanup deletes the ini before it merges the recording: the session kept what it said.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_offline_session_without_NVAPI_hooks_marks_its_recording_though_its_ini_goes_first(bool nvapiOff)
    {
        var store = Journal(["d3d12.dll", "scskiller.ini", "scskiller.db"]);
        var rec = store.LoadGame(_game.Id);
        rec.OfflineSession = rec.OfflineSession! with { NvapiOff = nvapiOff };
        store.SaveGame(_game.Id, rec);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        File.WriteAllText(ini, nvapiOff ? "[scskiller]\r\nframes=0\r\nnvapi=0\r\n" : "[scskiller]\r\nmode=record\r\n");
        File.WriteAllBytes(Path.Combine(_exeDir, "scskiller.db"), SharingTests.LocalRecording());
        Assert.Null(ScsKiller.CleanOfflineSession(store, _game.Id, _ => false, _ => { }));
        Assert.False(File.Exists(ini));
        Assert.True(File.Exists(Path.Combine(store.GameDir(_game.Id), "recording.db")));
        Assert.Equal(nvapiOff, File.Exists(Path.Combine(store.GameDir(_game.Id), ForkBuild.MinimalMarker)));
    }

    [Fact]
    public void An_offline_session_from_before_it_kept_its_NVAPI_state_reads_as_with_the_hooks()
    {
        var json = JsonSerializer.Serialize(new OfflineSession("g", "e", "i", [], [], NvapiOff: true), AppStore.Json);
        Assert.True(JsonSerializer.Deserialize<OfflineSession>(json, AppStore.Json)!.NvapiOff);
        Assert.False(JsonSerializer.Deserialize<OfflineSession>("""{"GameId":"g","Exe":"e","InstallDir":"i","Original":[],"Created":[]}""", AppStore.Json)!.NvapiOff);
    }

    /// <summary>The packs are filled from every game's recording and outlive a clear: one a held-back recording filled is
    /// never shared from this build; one only other recordings filled is.</summary>
    [Fact]
    public async Task An_upscaler_pack_filled_from_a_held_back_recording_stays_held_after_the_recording_is_cleared()
    {
        File.WriteAllText(Path.Combine(_exeDir, "scskiller.ini"), "[scskiller]\r\nframes=0\r\nnvapi=0\r\n");   // the recorder ran at Minimal
        var data = Path.Combine(_root, "data");
        var planner = new Planner(Path.Combine(data, "packs"), ScsKiller.SharedPackDir(data, GpuVendor.Nvidia));
        var (k, _, log) = await ForkSharer(planner: planner, vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Nvidia }));   // its packs' GPU
        File.Delete(Path.Combine(_exeDir, "scskiller.ini"));
        var rs = CommunityTests.RootSignature();
        string Pack(string dll, string source, char sha)
        {
            var pack = new MiddlewarePack("amd", dll, new string(sha, 40), 1000, gpu: "nvidia");
            pack.RootSignatures[CommunityTests.Sha1(rs)] = rs;
            pack.Add(new PsoDb.Rec('C', PsoDb.Compute(CommunityTests.Sha1(rs), CommunityTests.Sha1([.. SharingTests.Shader, (byte)sha]))), source);
            var path = planner.Packs!.PathOf("amd", dll, pack.Header.ContentHash);
            pack.Write(path);
            return path;
        }
        var held = Pack("held_dx12.dll", _game.Id, 'd');   // this game's recording filled it (and another's)
        var heldToo = MiddlewarePack.Read(held);
        heldToo.Add(new PsoDb.Rec('C', PsoDb.Compute(CommunityTests.Sha1(rs), CommunityTests.Sha1("another"u8.ToArray()))), "steam:480");
        heldToo.Write(held);
        Pack("clean_dx12.dll", "steam:480", 'e');   // only another game's
        bool Shared(string dll) { lock (log) return log.Any(l => l.StartsWith(dll + ": upscaler pack shared")); }

        await Share(k);
        Assert.True(Shared("clean_dx12.dll"));
        Assert.False(Shared("held_dx12.dll"));
        lock (log) Assert.Contains(log, l => l.StartsWith("upscaler packs: not shared from this unofficial build") && l.Contains("held_dx12.dll"));

        Assert.True(k.ClearRecording(_game.Id));   // the recording goes, what it put in the pack stays
        Assert.False(File.Exists(Path.Combine(k.Store.GameDir(_game.Id), ForkBuild.MinimalMarker)));
        heldToo.Add(new PsoDb.Rec('C', PsoDb.Compute(CommunityTests.Sha1(rs), CommunityTests.Sha1("more"u8.ToArray()))), "steam:480");
        heldToo.Write(held);   // grown since: it would go again
        await Share(k);
        Assert.False(Shared("held_dx12.dll"));
        Assert.Contains(Path.GetRelativePath(planner.Packs!.Dir, held), File.ReadAllLines(Path.Combine(data, ForkBuild.PacksHeld)));

        File.Delete(held);   // pruned: the list forgets it
        await Share(k);
        Assert.False(File.Exists(Path.Combine(data, ForkBuild.PacksHeld)));
    }

    /// <summary>A recording made before this build kept its markers: what its recorder's log and record still tell marks it,
    /// once.</summary>
    [Theory]
    [InlineData("loaded into x\r\nhooks off: frame timing (scskiller.ini frames=0), NVAPI and Aftermath (scskiller.ini nvapi=0); pipelines are still recorded\r\n", null, ForkBuild.MinimalMarker)]
    [InlineData("loaded into x\r\nhooks off: frame timing (scskiller.ini frames=0); pipelines are still recorded\r\n", null, null)]
    [InlineData("loaded into x\r\nnext: d3d12.scskiller-next.dll (the device and every export it has come from it)\r\n", null, ForkBuild.LayeredMarker)]
    [InlineData(null, RecorderLevel.Minimal, ForkBuild.MinimalMarker)]
    [InlineData(null, null, null)]
    public async Task A_recording_from_before_the_markers_is_marked_by_what_is_still_known(string? recorderLog, RecorderLevel? level, string? marker)
    {
        if (recorderLog != null) File.WriteAllText(Path.Combine(_exeDir, "scskiller.log"), recorderLog);
        var (k, uploads, _) = await ForkSharer(marked: false);
        if (level != null)
        {
            var rec = k.Store.LoadGame(_game.Id);
            rec.RecorderLevel = level;   // the crash guard stepped it down before the markers
            k.Store.SaveGame(_game.Id, rec);
        }
        Assert.False(k.ForkSettings.OldRecordingsMarked);
        await Share(k);
        Assert.True(k.ForkSettings.OldRecordingsMarked);
        Assert.True(new AppStore(Path.Combine(_root, "data")).LoadFork().OldRecordingsMarked);
        foreach (var m in new[] { ForkBuild.MinimalMarker, ForkBuild.LayeredMarker })
            Assert.Equal(m == marker, File.Exists(Path.Combine(k.Store.GameDir(_game.Id), m)));
        Assert.Equal(marker == null ? 1 : 0, uploads());
    }

    [Theory]
    [InlineData("[  12.0s] hooks off: NVAPI and Aftermath (SCSKILLER_NVAPI=0); pipelines are still recorded", true)]
    [InlineData("[  12.0s] hooks off: frame timing (scskiller.ini frames=0), NVAPI and Aftermath (scskiller.ini nvapi=0); pipelines are still recorded", true)]
    [InlineData("[  12.0s] hooks off: frame timing (scskiller.ini frames=0); pipelines are still recorded", false)]
    [InlineData("[  12.0s] NVAPI and Aftermath (on)", false)]
    public void The_recorders_log_tells_a_launch_without_NVAPI_hooks(string line, bool off) => Assert.Equal(off, ForkBuild.LoggedNvapiOff(line));

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
        var block = ForkBuild.UploadBlock(new(family, "1", null, "D3D12", false, null, ShipsRootSignatures: shipsRootSignatures), default);
        if (why == null) Assert.Null(block);
        else Assert.Contains(why, block);
        Assert.NotNull(ForkBuild.UploadBlock(null, default));   // not read yet: not shared
    }

    [Fact]
    public void What_a_recording_holds_blocks_it_whatever_read_the_game()
    {
        var beside = new ReShadeInstall(Path.Combine(_exeDir, "dxgi.dll"), true, null, null, []);
        var unloaded = beside with { Dll = Path.Combine(_exeDir, "ReShade64.dll") };
        Assert.True(beside.Copyable);
        Assert.False(unloaded.Copyable);
        Assert.Null(ForkBuild.ContentBlock(default));
        Assert.Null(ForkBuild.ContentBlock(new(ReShade: beside)));   // a copy reproduces it: its changes are 'W'-flagged or layered
        // what the recording holds: a clear takes it away
        Assert.Contains("NVAPI", ForkBuild.ContentBlock(new(RecordedMinimal: true)));
        Assert.Contains("clear the recording", ForkBuild.ContentBlock(new(RecordedMinimal: true)));
        Assert.Contains("alongside a mod", ForkBuild.ContentBlock(new(RecordedLayered: true)));
        Assert.Contains("clear the recording", ForkBuild.ContentBlock(new(RecordedLayered: true)));
        // how the recorder runs now: no clear helps
        foreach (var now in new ForkContent[] { new(NvapiOffNow: true), new(ChainedNow: true), new(ReShade: unloaded) })
            Assert.DoesNotContain("clear", ForkBuild.ContentBlock(now));
        Assert.Contains("NVAPI hooks", ForkBuild.ContentBlock(new(NvapiOffNow: true)));
        Assert.Contains("alongside a mod", ForkBuild.ContentBlock(new(ChainedNow: true)));
        Assert.Contains("ReShade", ForkBuild.ContentBlock(new(ReShade: unloaded)));
        Assert.Contains("clear", ForkBuild.ContentBlock(new(RecordedMinimal: true, NvapiOffNow: true)));   // the recording first
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
        Assert.Equal(["ShareRecordings", "OldRecordingsMarked"], JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name));
        Assert.NotEqual("settings.json", ForkBuild.SettingsFile);
        Assert.NotEqual("upload.dat", ForkBuild.UploadDevice);
    }
}
