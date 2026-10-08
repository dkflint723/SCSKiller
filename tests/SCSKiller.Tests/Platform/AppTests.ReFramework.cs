using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;

namespace SCSKiller.Tests.Platform;

// REFramework (RE Engine games) copies every dll next to the exe into _storage_ at each launch and reports the loaded ones'
// paths there; the dlls run from the exe's folder. The recorder works alongside it.
public partial class AppTests
{
    static readonly byte[] ReFrameworkDll = [.. Planning.MiddlewarePackTests.Pe("dinput8.dll"), .. "REFramework\0Copying DLL file: {}\0_storage_\0"u8];

    string Stored(string name = "d3d12.dll") => Path.Combine(Directory.CreateDirectory(Path.Combine(_exeDir, ReFramework.Storage)).FullName, name);

    [Fact]
    public async Task REFramework_is_known_by_its_dinput8_dll_and_noted_but_never_blocks()
    {
        var dinput8 = Path.Combine(_exeDir, "dinput8.dll");
        Assert.False(ReFramework.Detect(_exeDir));
        File.WriteAllBytes(dinput8, [.. Planning.MiddlewarePackTests.Pe("dinput8.dll"), .. "REFramework"u8]);   // a build that keeps no copies
        Assert.False(ReFramework.Detect(_exeDir));
        File.WriteAllBytes(dinput8, ReFrameworkDll);
        File.SetLastWriteTimeUtc(dinput8, DateTime.UtcNow.AddMinutes(1));
        Assert.True(ReFramework.Detect(_exeDir));

        var k = Managed();
        await k.ScanAsync(default);
        var s = k.Games.Single();
        Assert.Equal((true, true, null), (s.ReFramework, s.RecorderInstalled, s.RecorderSkip));
    }

    /// <summary>Removal deletes REFramework's copy of the recorder (any proxy of ours) and nothing else in _storage_: a
    /// foreign d3d12.dll copy, the copies of the game's dlls.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Removing_the_recorder_deletes_only_REFrameworks_copy_of_ours(bool ours)
    {
        var k = Managed();
        await k.ScanAsync(default);
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, "d3d12.dll")));
        byte[] copy = ours ? File.ReadAllBytes(Path.Combine(_exeDir, "d3d12.dll")) : Planning.MiddlewarePackTests.Pe("d3d12.dll", [1, 2, 3]);
        File.WriteAllBytes(Stored(), copy);
        File.WriteAllBytes(Stored("steam_api64.dll"), [1]);
        File.WriteAllBytes(Stored("dinput8.dll"), ReFrameworkDll);
        k.SetRecorderOverride(_game.Id, RecorderOverride.Off);
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.Equal(!ours, File.Exists(Stored()));
        if (!ours) Assert.Equal(copy, File.ReadAllBytes(Stored()));
        Assert.True(File.Exists(Stored("steam_api64.dll")) && File.Exists(Stored("dinput8.dll")));
        Assert.Equal(ours, File.ReadAllText(Path.Combine(k.Store.DataDir, "recorders.log")).Contains("deleted REFramework's copy"));
    }

    /// <summary>SCSKiller's uninstall takes REFramework's copy of the recorder out too, logged in recorders.log.</summary>
    [Fact]
    public async Task The_uninstall_hook_deletes_REFrameworks_copy_of_the_recorder()
    {
        var k = Managed();
        await k.ScanAsync(default);
        File.Copy(Path.Combine(_exeDir, "d3d12.dll"), Stored());
        k.StopWatchingInstalls();
        ScsKiller.RemoveAllRecorders(k.Store, new HashSet<string>());
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.False(File.Exists(Stored()));
        Assert.Contains("deleted REFramework's copy", File.ReadAllText(Path.Combine(k.Store.DataDir, "recorders.log")));
    }

    /// <summary>A chained mod's copy goes with the chain only while it is the mod as SCSKiller renamed it.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task REFrameworks_copy_of_a_chained_mod_goes_only_as_it_was_renamed(bool changed)
    {
        var mod = Path.Combine(_exeDir, "d3d12.dll");
        var bytes = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());
        File.WriteAllBytes(mod, bytes);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.SetRecordAlongsideMod(_game.Id, true);
        Assert.True(ScsKiller.IsOurProxy(mod));
        File.WriteAllBytes(Stored(ScsKiller.ChainName), changed ? [.. bytes, 1] : bytes);
        File.Copy(mod, Stored());
        k.SetRecorderOverride(_game.Id, RecorderOverride.Off);
        Assert.Equal(bytes, File.ReadAllBytes(mod));   // the mod is back
        Assert.False(File.Exists(Stored()));
        Assert.Equal(changed, File.Exists(Stored(ScsKiller.ChainName)));
    }

    /// <summary>A game without the recorder whose _storage_ still holds a copy of ours (taken out before SCSKiller deleted
    /// those): the scan deletes it, never a foreign one.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_scan_deletes_a_stale_REFramework_copy_of_ours_only(bool ours)
    {
        var copy = ours ? File.ReadAllBytes(_proxy) : Planning.MiddlewarePackTests.Pe("d3d12.dll", [4, 5]);
        File.WriteAllBytes(Stored(), copy);
        var k = Managed();
        k.Settings = k.Settings with { RecordAllGames = false };
        await k.ScanAsync(default);
        k.ReconcileRecorders();
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.Equal(!ours, File.Exists(Stored()));
    }

    /// <summary>REFramework's first copy of the recorder into _storage_ (the first launch after an install, before the
    /// recorder decides) doesn't disarm it; a new dll copied there does, as any change in the install.</summary>
    [Fact]
    public async Task REFrameworks_copy_of_the_recorder_doesnt_disarm_it_but_other_changes_there_do()
    {
        Directory.CreateDirectory(Path.Combine(_exeDir, ReFramework.Storage));
        var k = Managed();
        await k.ScanAsync(default);
        await AssertStaysArmed(k, _game);
        File.Copy(Path.Combine(_exeDir, "d3d12.dll"), Stored());
        File.Copy(Path.Combine(_exeDir, "d3d12.dll"), Stored(), true);   // the next launches overwrite it
        await AssertStaysArmed(k, _game);
        File.WriteAllBytes(Stored("newmod.dll"), [1]);
        await Until(() => !ArmedWithLedger(_game));
    }

    /// <summary>REFramework's _storage_ is a mirror of the dlls, never what ReShade loads: ReShade beside the exe, its ini,
    /// log and add-ons there; whatever _storage_ holds (a ReShade copy, add-ons, a log) doesn't count, and a warm's layer
    /// copies the game folder's files.</summary>
    [Fact]
    public void ReShade_under_REFramework_is_the_game_folders_not_its_storage_copies()
    {
        var g = FakeGame("test:refw-hdr", "Refw");
        string In(string name) => Path.Combine(g.InstallDir, name);
        var stored = Directory.CreateDirectory(In(ReFramework.Storage)).FullName;
        File.WriteAllBytes(In("dinput8.dll"), ReFrameworkDll);
        File.WriteAllBytes(In("dxgi.dll"), ReShadeDll);
        File.WriteAllBytes(In("renodx-ff7rebirth.addon64"), RenoDxAddon);
        WriteLog(g.InstallDir, RenoLog(In("renodx-ff7rebirth.addon64"), inject: false));
        foreach (var f in new[] { "dxgi.dll", "reshade64.asi" }) File.WriteAllBytes(Path.Combine(stored, f), ReShadeDll);
        File.WriteAllBytes(Path.Combine(stored, "renodx-newgame.addon64"), RenoDxAddon);
        WriteLog(stored, RenoLog(Path.Combine(stored, "renodx-newgame.addon64"), inject: true));
        Assert.True(ReFramework.Detect(g.InstallDir));

        var r = Core.Games.ReShade.Detect(g)!;
        Assert.Equal((In("dxgi.dll"), In("ReShade.log"), true), (r.Dll, r.Log, r.BesideExe));
        Assert.Equal([In("renodx-ff7rebirth.addon64")], r.Addons.Select(a => a.Path));
        Assert.Equal((AddonKind.ReplacesShaders, true, false), (r.ShaderMod!.Kind, r.Layered, r.Blocks));   // the root log's verdict
        var layer = Core.Games.ReShade.Stage(r, Path.Combine(_root, "layer"));
        Assert.Equal(["dxgi.dll", "renodx-ff7rebirth.addon64"], Directory.GetFiles(layer).Select(Path.GetFileName).Order());
        Assert.Equal(ReShadeDll, File.ReadAllBytes(Path.Combine(layer, "dxgi.dll")));
    }

    /// <summary>The recorder under REFramework's path rewrite (selftest's spoof_exe_dir, as kananlib does it): it reports
    /// _storage_\d3d12.dll, runs from the exe's folder, and records under the exe folder's attestation into the exe's
    /// folder. One in _storage_ alone doesn't count, and an anti-cheat marker there refuses.</summary>
    [Theory]
    [InlineData("", 1)]
    [InlineData("armed in storage", 0)]
    [InlineData("marker in storage", 0)]
    public void The_proxy_under_REFrameworks_path_rewrite_uses_the_game_folder(string variant, int computes)
    {
        UseLiveLedger();   // the built proxy reads the live one
        if (OwnWarmExe() == null) return;
        var exeDir = Path.Combine(_root, "refw");
        var storage = Path.Combine(exeDir, ReFramework.Storage);
        var r = Selftest(exeDir, "refw once", armed: variant == "armed in storage" ? null : Armed, ledger: variant == "armed in storage" ? l =>
        {
            ScsKiller.WriteAttestation(Path.Combine(exeDir, "selftest.exe"));
            Directory.CreateDirectory(storage);
            File.Move(Path.Combine(exeDir, ScsKiller.ArmedFile), Path.Combine(storage, ScsKiller.ArmedFile));
        } : variant == "marker in storage" ? _ => File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(storage).FullName, "BEClient_x64.dll"), [0]) : null)!.Value;
        Assert.Contains(Path.Combine(storage, "d3d12.dll"), r.Output);   // the path the loader reports
        Assert.Contains("created 0x00000000", r.Output);
        Assert.Equal(computes, r.Computes);
        Assert.Equal(computes > 0, File.Exists(Path.Combine(exeDir, "scskiller_creates.csv")));
        foreach (var f in new[] { "scskiller.db", "scskiller.log", "scskiller_creates.csv" }) Assert.False(File.Exists(Path.Combine(storage, f)));
        if (computes > 0) Assert.Contains($"through REFramework's {storage}\\", File.ReadAllText(Path.Combine(exeDir, "scskiller.log")));
    }

    /// <summary>Two copies of the recorder in one process: the first records (everything, both devices' pipelines run its
    /// hooks), the other only forwards: devices through either work, one log, nothing next to the second.</summary>
    [Fact]
    public void Two_copies_of_the_proxy_in_one_process_make_one_recorder()
    {
        UseLiveLedger();
        if (OwnWarmExe() is not { } warm) return;
        var exeDir = Path.Combine(_root, "twice");
        var second = Directory.CreateDirectory(Path.Combine(exeDir, "second")).FullName;
        File.Copy(Path.Combine(Path.GetDirectoryName(warm)!, "d3d12.dll"), Path.Combine(second, "d3d12.dll"));
        File.WriteAllText(Path.Combine(second, "scskiller.ini"), "[scskiller]\r\nmode=record\r\nframes=0\r\n");
        var r = Selftest(exeDir, "refw twice")!.Value;
        Assert.Contains("copies 2", r.Output);
        Assert.Equal(2, r.Output.Split('\n').Count(l => l.StartsWith("created 0x00000000", StringComparison.Ordinal)));
        Assert.Equal(2, r.Computes);
        var log = File.ReadAllLines(Path.Combine(exeDir, "scskiller.log"));
        Assert.Single(log, l => l.Contains("loaded into"));
        Assert.Single(log, l => l.Contains("another copy of the recorder is loaded") && l.Contains(second));
        Assert.Equal(["d3d12.dll", "scskiller.ini"], Directory.GetFiles(second).Select(Path.GetFileName).Order());
    }
}
