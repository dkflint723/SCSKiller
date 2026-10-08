using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;

namespace SCSKiller.Tests.Platform;

// Frame generation beside the recorder (noted; pipelines only from the start where the recorder isn't needed), the proxy's
// frames=0 / nvapi=0, games whose files list every pipeline (no recorder from "record all"), and the count of new
// pipelines for such a game.
public partial class AppTests
{
    static readonly EngineInfo Pspc = new("Square Enix PSPC", "0x0300000A", null, "D3D12", false, null, ShipsRootSignatures: true);

    [Fact]
    public void Frame_generation_is_known_by_its_files_in_the_exe_folder_or_the_install_root()
    {
        var dir = Path.Combine(_root, "fg");
        string? Detect(string[] files, string? ini = null, string? root = null)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            foreach (var f in files) File.WriteAllBytes(Path.Combine(dir, f), Planning.MiddlewarePackTests.Pe(f));
            if (ini != null) File.WriteAllText(Path.Combine(dir, "OptiScaler.ini"), ini);
            return FrameGen.Detect(dir, root);
        }
        Assert.Equal("Streamline DLSS-G", Detect(["sl.interposer.dll", "sl.dlss_g.dll", "sl.common.dll"]));
        Assert.Equal("Streamline DLSS-G", Detect(["nvngx_dlssg.dll"]));
        Assert.Equal("Streamline DLSS-G", Detect(["SL.Interposer.dll", "sl.DLSS_G.dll"]));
        Assert.Null(Detect(["sl.interposer.dll", "sl.dlss.dll", "sl.reflex.dll", "nvngx_dlss.dll"]));   // Streamline without frame generation
        Assert.Equal("DLSS-G to FSR3 mod", Detect(["dlssg_to_fsr3_amd_is_better.dll"]));
        Assert.Equal("OptiScaler frame generation", Detect(["OptiScaler.dll"], "[FrameGen]\r\nFGType=optifg ; frames\r\n"));
        Assert.Null(Detect(["OptiScaler.dll"], "[FrameGen]\r\nFGType=nofg\r\n"));
        Assert.Null(Detect(["OptiScaler.dll"], "[FrameGen]\r\nFGType=auto\r\n"));
        Assert.Equal("OptiScaler frame generation", Detect(["OptiScaler.dll", "libxess_fg.dll"], "[FrameGen]\r\nFGType=auto\r\n"));
        // a newer ini: FGOutput with Enabled decides, FGType aside; an older one's FGType=optifg is present without [OptiFG] Enabled
        Assert.Equal("OptiScaler frame generation", Detect(["OptiScaler.dll"], "[FrameGen]\r\nEnabled=true\r\nFGOutput=fsrfg\r\n"));
        Assert.Null(Detect(["OptiScaler.dll"], "[FrameGen]\r\nEnabled=false\r\nFGOutput=fsrfg\r\nFGType=optifg\r\n"));
        Assert.Null(Detect(["OptiScaler.dll"], "[FrameGen]\r\nEnabled=true\r\nFGOutput=nofg\r\n"));
        Assert.Equal("OptiScaler frame generation", Detect(["OptiScaler.dll"], "[FrameGen]\r\nFGType=optifg\r\n[OptiFG]\r\nEnabled=false\r\n"));
        Assert.Equal("OptiScaler frame generation", Detect(["OptiScaler.dll"], "[FrameGen]\r\nFGType=auto\r\n[OptiFG]\r\nEnabled=true\r\n"));
        Assert.Null(Detect(["libxess_fg.dll", "amd_fidelityfx_framegeneration_dx12.dll"]));   // the libraries alone: the game's own, behind its swap chain
        Assert.Null(Detect([]));
        // the install root, a level above the exe
        var root = Path.Combine(_root, "fg-root");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "nvngx_dlssg.dll"), [0]);
        Assert.Equal("Streamline DLSS-G", Detect([], root: root));
    }

    /// <summary>The built proxy on WARP (`selftest hooks`): frames=0 / nvapi=0, in scskiller.ini or as SCSKILLER_FRAMES /
    /// SCSKILLER_NVAPI, leave the swap chain's and DXGI factory's vtables and NVAPI's entry points as they were, write no
    /// frame log and say so in one log line; the compute PSO is recorded either way.</summary>
    [Fact]
    public void The_proxy_installs_no_frame_or_nvapi_hooks_when_told_not_to()
    {
        if (OwnWarmExe() is not { } warmExe) return;
        var bin = Path.GetDirectoryName(warmExe)!;
        (string Out, string Log) Run(string name, string ini, bool env = false)
        {
            var dir = Path.Combine(_root, "hooks-" + name);
            Directory.CreateDirectory(dir);
            foreach (var f in new[] { "selftest.exe", "d3d12.dll" }) File.Copy(Path.Combine(bin, f), Path.Combine(dir, f));
            File.WriteAllText(Path.Combine(dir, "scskiller.ini"), $"[scskiller]\r\nmode=record\r\n{ini}");
            var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(dir, "selftest.exe"), "hooks") { RedirectStandardOutput = true };
            if (env) (psi.Environment["SCSKILLER_FRAMES"], psi.Environment["SCSKILLER_NVAPI"]) = ("0", "0");
            using var p = System.Diagnostics.Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
            Assert.Contains("computes 1", o);
            return (o, File.ReadAllText(Path.Combine(dir, "scskiller.log")));
        }
        var on = Run("on", "");
        Assert.Contains("present_hooked 1\r\nfactory_hooked 1", on.Out.ReplaceLineEndings("\r\n"));
        Assert.Contains("frames_file 1", on.Out);
        Assert.DoesNotContain("hooks off", on.Log);
        var nvapi = !on.Out.Contains("nvapi_hooked -1");   // no NVIDIA driver here: nothing to hook either way
        if (nvapi) Assert.Contains("nvapi_hooked 1", on.Out);

        foreach (var (name, ini, env, line) in new[] { ("ini", "frames=0\r\nnvapi=0\r\n", false, "hooks off: frame timing (scskiller.ini frames=0), NVAPI and Aftermath (scskiller.ini nvapi=0)"),
                     ("env", "", true, "hooks off: frame timing (SCSKILLER_FRAMES=0), NVAPI and Aftermath (SCSKILLER_NVAPI=0)") })
        {
            var off = Run(name, ini, env);
            Assert.Contains("present_hooked 0\r\nfactory_hooked 0", off.Out.ReplaceLineEndings("\r\n"));
            Assert.Contains(nvapi ? "nvapi_hooked 0" : "nvapi_hooked -1", off.Out);
            Assert.Contains("frames_file 0", off.Out);
            Assert.Contains(line, off.Log);
            Assert.DoesNotContain("nvapi: hooks installed", off.Log);
        }
        var framesOnly = Run("frames", "frames=0\r\n");
        Assert.Contains(nvapi ? "nvapi_hooked 1" : "nvapi_hooked -1", framesOnly.Out);
        Assert.Contains("present_hooked 0", framesOnly.Out);
        var nvapiOnly = Run("nvapi", "nvapi=0\r\n");
        Assert.Contains("present_hooked 1", nvapiOnly.Out);
        Assert.Contains(nvapi ? "nvapi_hooked 0" : "nvapi_hooked -1", nvapiOnly.Out);
    }

    /// <summary>Frame generation's files ship whether it's on or not: they take no hooks off by themselves (the crash guard
    /// does, if the game closes early); a user's own frames=0 / nvapi=0 still does.</summary>
    [Fact]
    public async Task With_frame_generation_the_recorder_keeps_its_hooks_and_the_game_page_names_the_crash_guard()
    {
        var k = Managed();
        await k.ScanAsync(default);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.Null(k.Games.Single().FrameGen);

        foreach (var f in new[] { "sl.interposer.dll", "sl.dlss_g.dll", "nvngx_dlssg.dll" }) File.WriteAllBytes(Path.Combine(_exeDir, f), Planning.MiddlewarePackTests.Pe(f));
        k.ReconcileRecorders();
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal(("Streamline DLSS-G", true, RecorderLevel.Full, (string?)null, false), (s.FrameGen, s.RecorderEffective, s.RecorderLevel, s.RecorderLevelReason, s.RecorderSteppedDown));
        Assert.DoesNotContain("frames=", File.ReadAllText(ini));   // the hooks stay
        Assert.DoesNotContain("nvapi=", File.ReadAllText(ini));
        Assert.Equal("frame generation (Streamline DLSS-G) is present: if the game closes early with the recorder, it switches to pipelines only", ScsKiller.FrameGenNote(s));
        Assert.Equal("frame generation (Streamline DLSS-G) is present: the recorder records pipelines only, no frame times", ScsKiller.FrameGenNote(s with { RecorderLevel = RecorderLevel.Minimal }));

        // the user's own switches: left alone
        var own = "[scskiller]\r\nmode=record\r\nframes=0\r\nnvapi=0\r\n";
        File.WriteAllText(ini, own);
        k.ReconcileRecorders();
        k.RefreshGame(_game.Id);
        Assert.Equal(own, File.ReadAllText(ini));
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, "d3d12.dll")));

        // the recorder's report without a frame log: the session's counts, no frame times, nothing claimed missing
        File.WriteAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), "10.0,G,0,0,50.0\n11.0,s,0,0,0.2\n12.0,C,1,1,0.5\n");
        k.RefreshGame(_game.Id);
        s = k.Games.Single();
        Assert.Equal((3L, (FrameReport?)null), (s.LastSession!.Requests, s.LastFrames));

        foreach (var f in new[] { "sl.interposer.dll", "sl.dlss_g.dll", "nvngx_dlssg.dll" }) File.Delete(Path.Combine(_exeDir, f));
        k.ReconcileRecorders();
        k.RefreshGame(_game.Id);
        Assert.Null(k.Games.Single().FrameGen);
    }

    /// <summary>FINAL FANTASY XVI's case: frame generation beside a game whose files list every pipeline (recorded only when
    /// the user asks) starts the recorder at pipelines only; an early failure there takes it out, and "Try again" or another
    /// build puts it back at pipelines only, not in full.</summary>
    [Fact]
    public async Task Beside_frame_generation_a_game_that_doesnt_need_the_recorder_starts_it_at_pipelines_only()
    {
        var (k, running, poll, now) = Guarded(new FakeReader(Pspc));
        foreach (var f in new[] { "sl.interposer.dll", "sl.dlss_g.dll" }) File.WriteAllBytes(Path.Combine(_exeDir, f), Planning.MiddlewarePackTests.Pe(f));
        await k.ScanAsync(default);
        var (dll, ini, exe) = (Path.Combine(_exeDir, "d3d12.dll"), Path.Combine(_exeDir, "scskiller.ini"), Path.GetFileName(_game.ExePath));
        var s = k.Games.Single();
        const string start = "frame generation (Streamline DLSS-G) is present and the recorder isn't needed for this game: it records pipelines only (no frame times)";
        Assert.Equal((true, false, RecorderLevel.Minimal, start, false), (s.RecordingNotNeeded, s.RecorderInstalled, s.RecorderLevel, s.RecorderLevelReason, s.RecorderSteppedDown));
        Assert.Null(k.Store.LoadGame(_game.Id).RecorderLevel);

        k.InstallRecorder(_game.Id);
        var text = File.ReadAllText(ini);
        Assert.Contains("\r\nframes=0\r\n", text);
        Assert.Contains("\r\nnvapi=0\r\n", text);
        Assert.Contains("frame generation is present and the recorder isn't needed for this game", text);
        Assert.Equal("frame generation (Streamline DLSS-G) is present: the recorder records pipelines only, no frame times", ScsKiller.FrameGenNote(k.Games.Single()));

        Play(running, now, poll, _exeDir, exe, 6);   // closed early at its start level: out
        s = k.Games.Single();
        Assert.Equal((RecorderLevel.Off, ScsKiller.SkipCrashed, false, true), (s.RecorderLevel, s.RecorderSkip, s.RecorderInstalled, s.RecorderSteppedDown));
        Assert.Equal("the game closed early with it", s.RecorderSkip);   // once here: Settings' count and the CLI don't say twice
        Assert.Equal("Fake Game closed shortly after starting (6 s): the recorder was taken out for this game", s.RecorderLevelReason);
        Assert.False(File.Exists(dll));

        k.ResetRecorderHealth(_game.Id);   // Try again: back to pipelines only
        s = k.Games.Single();
        Assert.Equal((RecorderLevel.Minimal, start, false, true), (s.RecorderLevel, s.RecorderLevelReason, s.RecorderSteppedDown, s.RecorderInstalled));
        Assert.Contains("\r\nframes=0\r\n", File.ReadAllText(ini));
        Assert.Contains("the recorder records pipelines only again (Try again)", File.ReadAllText(Path.Combine(k.Store.DataDir, "recorders.log")));

        Play(running, now, poll, _exeDir, exe, 6);
        Assert.Equal(RecorderLevel.Off, k.Games.Single().RecorderLevel);
        File.WriteAllBytes(_game.ExePath, new byte[5000]);   // a game update
        await k.RescanAsync(default);
        s = k.Games.Single();
        Assert.Equal((RecorderLevel.Minimal, false, true), (s.RecorderLevel, s.RecorderSteppedDown, s.RecorderInstalled));
        Assert.Null(k.Store.LoadGame(_game.Id).RecorderLevel);
        Assert.Contains("\r\nframes=0\r\n", File.ReadAllText(ini));
    }

    [Fact]
    public async Task A_game_whose_files_list_every_pipeline_isnt_recorded_by_record_all_and_loses_its_recorder()
    {
        var k = Killer(new FakeReader(Unreal));   // first read as another engine: "record all" put the recorder in
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = true;
        await k.ScanAsync(default);
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, "d3d12.dll")));
        var before = Directory.GetFiles(_exeDir).Where(f => !Path.GetFileName(f).StartsWith("scskiller", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(f) != "d3d12.dll").Order().ToList();

        k = Killer(new FakeReader(Pspc));   // its pipeline list read now: a recording adds nothing on this GPU
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = true;
        await k.RescanAsync(default);
        var s = k.Games.Single();
        Assert.Equal((true, false, false, null), (s.RecordingNotNeeded, s.RecorderEffective, s.RecorderInstalled, s.RecorderSkip));
        Assert.Equal(before, Directory.GetFiles(_exeDir).Order());   // only the recorder's files went
        Assert.Equal(RecorderOverride.Default, k.Store.LoadGame(_game.Id).Recorder);

        // still the user's to turn on, per game
        k.InstallRecorder(_game.Id);
        Assert.Equal((true, true), (k.Games.Single().RecorderInstalled, k.Games.Single().RecorderEffective));
        k.ReconcileRecorders();
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, "d3d12.dll")));

        Assert.False(ScsKiller.RecordingNotNeeded(Pspc, new PlanCheck(Readiness.NeedsRecording, Planner.Record), new FakeVendor(Gpu).Caps));
        Assert.False(ScsKiller.RecordingNotNeeded(Unreal, new PlanCheck(Readiness.Ready, Planner.NoRecording), new FakeVendor(Gpu).Caps));
        Assert.True(ScsKiller.RecorderEffective(RecorderOverride.On, true, null, notNeeded: true));
        Assert.False(ScsKiller.RecorderEffective(RecorderOverride.Default, true, null, notNeeded: true));
        // "Use default" on its page: off here, and so the switch while it runs (it showed on until the removal finished)
        Assert.Equal((false, false), (ScsKiller.RecordsByDefault(true, s), ScsKiller.RecordsByDefault(true, s with { NoStutter = "none", RecordingNotNeeded = false })));
        Assert.Equal((true, false), (ScsKiller.RecordsByDefault(true, s with { RecordingNotNeeded = false }), ScsKiller.RecordsByDefault(false, s with { RecordingNotNeeded = false })));
    }

    /// <summary>A game whose index names each pipeline's root signature (FINAL FANTASY XVI's list), on a state-independent
    /// cache, compiled through a ReShade layer: its plan creates pipeline P with root signature R, served by the install,
    /// not in the plan. The warm took P whole. A recording of P (R's bytes with it), of P's shaders under other state, and
    /// of what the layer made of P (a 'W' naming both) adds nothing to compile; a new tuple does.</summary>
    [Fact]
    public async Task A_recording_of_a_shipped_pipeline_lists_plan_adds_nothing_but_a_new_tuple()
    {
        File.WriteAllBytes(Path.Combine(_exeDir, "dxgi.dll"), ReShadeDll);
        File.WriteAllBytes(Path.Combine(_exeDir, "renodx-ff7rebirth.addon64"), RenoDxAddon);
        byte[] rsBytes = "root signature R"u8.ToArray(), layerRs = "R with the layer's constant"u8.ToArray();
        var (rs, cs, cs2, lrs) = (Sha(rsBytes), new string('1', 40), new string('2', 40), Sha(layerRs));
        var index = new ShaderIndex(new string('c', 40), ["PCD3D_SM6"], new Dictionary<string, ShaderInfo> { [cs] = null!, [cs2] = null! },
            [new ShaderMap("p", "Pipelines", "PCD3D_SM6", [cs], IsPipeline: true, RootSignature: rs)]);
        var plan = new PsoDb.Rec('C', PsoDb.Compute(rs, cs));
        var k = Killer(new IndexReader(Pspc, index), new FakePlanner(records: [plan]), vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Nvidia }));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        await Compile(k);
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        Assert.NotNull(rec.WarmedLayer);
        Assert.Contains(plan.Key, KeyFiles.Set(Path.Combine(k.Store.GameDir(_game.Id), rec.WarmKeysFile!))!);   // whole: R comes from the install
        Assert.Equal([rs], Sharing.ShippedRootSignatures(k.Store.GameDir(_game.Id), index.ContentHash));

        var state = PsoDb.Compute(rs, cs);
        state[^4] = 1;   // other flags: another record, the same compile on NVIDIA
        var layered = new PsoDb.Rec('C', PsoDb.Compute(lrs, cs));
        using (var f = new FileStream(Path.Combine(_exeDir, "scskiller.db"), FileMode.Append))
        {
            PsoDb.Write(f, 'B', [.. Convert.FromHexString(rs), .. rsBytes]);
            PsoDb.Write(f, 'B', [.. Convert.FromHexString(lrs), .. layerRs]);
            PsoDb.Write(f, 'C', plan.Payload);
            PsoDb.Write(f, 'C', state);
            PsoDb.Write(f, 'C', layered.Payload);
            PsoDb.Write(f, 'W', [.. Convert.FromHexString(layered.Key), .. Convert.FromHexString(plan.Key)]);
        }
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Warmed, 0L, 0L), (s.Status, s.RecordedSinceWarm, s.NewPipelines ?? 0));

        Record("a shader of no plan pipeline"u8.ToArray());   // its shader is neither recorded nor in the install: not counted
        using (var f = new FileStream(Path.Combine(_exeDir, "scskiller.db"), FileMode.Append)) PsoDb.Write(f, 'C', PsoDb.Compute(rs, cs2));
        k.RefreshGame(_game.Id);
        Assert.Equal(1, k.Games.Single().RecordedSinceWarm);   // a pipeline the plan doesn't have, its shader in the install: counted

        static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));
    }

    [Fact]
    public void Covered_takes_a_plan_pipelines_tuple_a_layers_change_and_a_root_signature_the_install_gives_back()
    {
        var dir = Path.Combine(_root, "covered");
        Directory.CreateDirectory(dir);
        var (rs, cs, other) = (new string('a', 40), new string('1', 40), new string('2', 40));
        var planned = new PsoDb.Rec('C', PsoDb.Compute(rs, cs));
        var planFile = Path.Combine(dir, "plan.bin");
        PlanFile.Write(new Plan(_game.Id, "content-1", "PCD3D_SM6", "fake-1", new PlanStats(0, 1, 1, 1, true), planFile), [planned]);
        var state = PsoDb.Compute(rs, cs);
        state[^4] = 1;
        var (driver, own, fresh) = (new PsoDb.Rec('C', PsoDb.Compute(new string('b', 40), cs)), new PsoDb.Rec('C', PsoDb.Compute(new string('b', 40), other)), new PsoDb.Rec('C', PsoDb.Compute(rs, other)));
        var db = Path.Combine(dir, "recording.db");
        using (var f = File.Create(db))
        {
            foreach (var r in new[] { planned, new PsoDb.Rec('C', state), driver, own, fresh }) PsoDb.Write(f, r.Tag, r.Payload);
            PsoDb.Write(f, 'W', [.. Convert.FromHexString(driver.Key), .. Convert.FromHexString(planned.Key)]);
            PsoDb.Write(f, 'W', [.. Convert.FromHexString(own.Key), .. new byte[20]]);   // the layer's own create
        }
        var stateKey = new PsoDb.Rec('C', state).Key;
        HashSet<string> whole = [planned.Key], marked = [WarmInputs.Token(planned.Key + "!")];

        Assert.Equal(new[] { stateKey, driver.Key }.Order(), WarmInputs.Covered([db], planFile, whole, stateIndependent: true, sameLayer: true).Order());
        Assert.Equal([driver.Key], WarmInputs.Covered([db], planFile, whole, stateIndependent: false, sameLayer: true));   // AMD: the state counts
        Assert.Equal([stateKey], WarmInputs.Covered([db], planFile, whole, stateIndependent: true, sameLayer: false));     // another layer now
        Assert.Empty(WarmInputs.Covered([db], planFile, new HashSet<string>(), stateIndependent: true, sameLayer: false));                    // the plan's pipeline wasn't warmed
        Assert.Empty(WarmInputs.Covered([db], planFile, marked, stateIndependent: true, sameLayer: false));                // ...or with a blob missing
        // taken with R counted missing, R and the shader from the install: the warm had them
        var gave = WarmInputs.Covered([db], planFile, marked, stateIndependent: true, sameLayer: false, installed: h => h == rs || h == cs);
        Assert.Equal(new[] { planned.Key, stateKey }.Order(), gave.Order());
        Assert.Empty(WarmInputs.Covered([db], planFile, marked, stateIndependent: true, sameLayer: false, installed: h => h == cs));
    }
}
