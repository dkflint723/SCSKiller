using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

// The crash guard (RecorderHealth): a launch that closed early with the recorder steps it down for the game, Full ->
// Minimal (frames=0, nvapi=0) -> Off (taken out); and the recorder's files written through temp names.
public partial class AppTests
{
    const long T0 = 1_700_000_000_000;

    static FrameReport FramesOf(long launch, double seconds) => new(TimeSpan.FromSeconds(seconds), TimeSpan.Zero, 10, 60, [], [], launch);

    [Fact]
    public void An_early_failure_is_a_launch_without_an_end_that_lasted_under_the_threshold()
    {
        var csv = Path.Combine(_root, "creates.csv");
        Directory.CreateDirectory(_root);
        var start = DateTimeOffset.FromUnixTimeMilliseconds(T0);
        PlayWindow Ran(double seconds) => new(start.AddSeconds(-2), start.AddSeconds(seconds));
        RecorderVerdict? Judge(PlayWindow? played = null, FrameReport? frames = null, long after = T0 - 60_000, long seen = 0,
            string exe = "Fake.exe") => RecorderHealth.Judge(csv, exe, after, seen, played, frames);
        File.WriteAllText(csv, $"#session,{T0 - 600_000},Fake.exe\n#clock,90.0\n1000.0,G,0,0,50.0\n#end,{T0 - 300_000},90000.0\n"   // an older launch
            + $"#session,{T0},Fake.exe\n#clock,100.0\n1000.0,G,0,0,50.0\n4000.0,S,1,1,0.5\n");

        Assert.Equal(new RecorderVerdict(T0, TimeSpan.FromSeconds(4)), Judge(Ran(4)));         // watched: it exited 4 s after starting
        Assert.Equal(new RecorderVerdict(T0, null), Judge(Ran(120)));                          // played two minutes
        Assert.Equal(new RecorderVerdict(T0, null), Judge());                                  // no watched run, no frame of it: can't tell
        Assert.Equal(new RecorderVerdict(T0, null), Judge(new(start.AddHours(-2), start.AddHours(-1))));   // an older run's window
        Assert.Equal(TimeSpan.FromSeconds(4), Judge(frames: FramesOf(T0, 3))!.EarlyFailure);   // its frames end within it
        Assert.Null(Judge(frames: FramesOf(T0, 600))!.EarlyFailure);                          // ten minutes of frames
        Assert.Null(Judge(frames: FramesOf(T0 - 600_000, 600))!.EarlyFailure);               // another launch's frames: can't tell
        Assert.Null(Judge(frames: FramesOf(T0 - 600_000, 3))!.EarlyFailure);                 // ...short ones too
        Assert.Null(Judge(Ran(4), seen: T0));                                                  // judged once: never again
        Assert.Null(Judge(Ran(4), after: T0));                                                 // from before the recorder's install or level
        Assert.Null(Judge(Ran(4), exe: "Other.exe"));                                          // only another exe's launches

        File.AppendAllText(csv, $"#end,{T0 + 4_000},4100.0\n");
        Assert.Equal(new RecorderVerdict(T0, null), Judge(Ran(4), FramesOf(T0, 3)));          // it exited cleanly: never a failure

        // two hours unwatched, every pipeline created in the first 30 s, no frame of it logged (held, capped, no present hook)
        File.WriteAllText(csv, $"#session,{T0},Fake.exe\n#clock,100.0\n1000.0,G,0,0,50.0\n30000.0,S,1,1,0.5\n");
        Assert.Equal(new RecorderVerdict(T0, null), Judge());
        Assert.Equal(new RecorderVerdict(T0, null), Judge(frames: FramesOf(T0 - 600_000, 3)));
        Assert.Null(Judge(frames: FramesOf(T0, 7200))!.EarlyFailure);
        Assert.Equal(TimeSpan.FromSeconds(30), Judge(frames: FramesOf(T0, 20))!.EarlyFailure);   // its own frames end early: the last create counts
        // frame generation's swap chain took the Present hook off at 20 s: the frame log ends there, the launch may not have
        File.AppendAllText(csv, $"#frames_off,{T0 + 20_000},20100.0\n");
        Assert.Equal(new RecorderVerdict(T0, null), Judge(frames: FramesOf(T0, 20)));
        Assert.Equal(TimeSpan.FromSeconds(4), Judge(Ran(4), FramesOf(T0, 3))!.EarlyFailure);   // the watched run still tells

        File.WriteAllText(csv, $"#session,{T0},Fake.exe\n#clock,100.0\n");                     // no create at all
        Assert.Equal(new RecorderVerdict(T0, null), Judge(frames: FramesOf(T0, 3)));
        Assert.Equal(TimeSpan.FromSeconds(4), Judge(Ran(4))!.EarlyFailure);                   // but the watched run tells
        File.WriteAllText(csv, "1000.0,G,0,0,50.0\n");                                          // an older proxy: no #session, no start
        Assert.Null(Judge(Ran(4), FramesOf(T0, 3)));
        File.Delete(csv);
        Assert.Null(Judge(Ran(4)));

        Assert.Equal("Fake Game closed 4 s after starting with the recorder: it now records pipelines only for this game (no frame times)",
            RecorderHealth.Note("Fake Game", RecorderLevel.Minimal, TimeSpan.FromSeconds(4.2)));
        Assert.Equal("Fake Game closed again shortly after starting (3 s): the recorder was taken out for this game",
            RecorderHealth.Note("Fake Game", RecorderLevel.Off, TimeSpan.FromSeconds(3)));
    }

    /// <summary>The game watched from start to exit: a run of <paramref name="seconds"/> whose launch the recorder marked
    /// (<paramref name="end"/>: with its #end; <paramref name="marks"/>: the recorder's lines after its creates, from its start).</summary>
    static void Play(HashSet<string> running, Func<DateTimeOffset> now, Action<int> poll, string exeDir, string exe, int seconds, bool end = false,
        Func<long, string>? marks = null)
    {
        poll(3);
        running.Add(exe);
        poll(3);
        var start = now().ToUnixTimeMilliseconds();
        File.AppendAllText(Path.Combine(exeDir, "scskiller_creates.csv"), $"#session,{start},{exe}\n#clock,50.0\n500.0,G,0,0,40.0\n{seconds * 900.0:0.0},S,1,1,0.5\n" + marks?.Invoke(start)
            + (end ? $"#end,{start + seconds * 1000L},{seconds * 1000.0:0.0}\n" : ""));
        for (var t = 0; t < seconds; t += 3) poll(3);
        running.Clear();
        for (int i = 0; i < ScsKiller.ExitPolls; i++) poll(3);
    }

    (ScsKiller K, HashSet<string> Running, Action<int> Poll, Func<DateTimeOffset> Now) Guarded(IEngineReader? reader = null)
    {
        var k = Managed(reader);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clock = DateTimeOffset.FromUnixTimeMilliseconds(T0);
        (k.RunningGameExes, k.Clock) = (() => running.ToHashSet(StringComparer.OrdinalIgnoreCase), () => clock);
        return (k, running, s => { clock = clock.AddSeconds(s); k.PollGames(); }, () => clock);
    }

    List<string> NotOurs() => Directory.GetFiles(_exeDir).Where(f => !Path.GetFileName(f).StartsWith("scskiller", StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(f) != "d3d12.dll").Order().ToList();

    [Fact]
    public async Task A_game_that_closes_early_with_the_recorder_records_pipelines_only_then_loses_it()
    {
        var (k, running, poll, now) = Guarded();
        await k.ScanAsync(default);
        var (dll, ini, exe) = (Path.Combine(_exeDir, "d3d12.dll"), Path.Combine(_exeDir, "scskiller.ini"), Path.GetFileName(_game.ExePath));
        Assert.True(ScsKiller.IsOurProxy(dll));
        var before = NotOurs();

        Play(running, now, poll, _exeDir, exe, 300);   // five minutes, no #end (as Unreal ends itself): fine
        Play(running, now, poll, _exeDir, exe, 6, end: true);   // quit at once, cleanly: fine
        Assert.Equal((RecorderLevel.Full, (string?)null), (k.Games.Single().RecorderLevel, k.Games.Single().RecorderLevelReason));
        Assert.DoesNotContain("frames=0", File.ReadAllText(ini));

        Play(running, now, poll, _exeDir, exe, 6);   // closed 6 s after starting, no #end
        var s = k.Games.Single();
        Assert.Equal((RecorderLevel.Minimal, true, true, (string?)null), (s.RecorderLevel, s.RecorderInstalled, s.RecorderEffective, s.RecorderSkip));
        Assert.StartsWith("Fake Game closed ", s.RecorderLevelReason);
        Assert.EndsWith(" after starting with the recorder: it now records pipelines only for this game (no frame times)", s.RecorderLevelReason);
        var text = File.ReadAllText(ini);
        Assert.Contains("\r\nframes=0\r\n", text);
        Assert.Contains("\r\nnvapi=0\r\n", text);
        Assert.Contains(s.RecorderLevelReason!, File.ReadAllText(Path.Combine(k.Store.DataDir, "recorders.log")));
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Equal(_game.Version ?? $"{new FileInfo(_game.ExePath).Length}:{new FileInfo(_game.ExePath).LastWriteTimeUtc.Ticks}", rec.RecorderLevelBuild);
        k.RefreshGame(_game.Id);   // judged once
        await k.RescanAsync(default);
        Assert.Equal(RecorderLevel.Minimal, k.Games.Single().RecorderLevel);

        Play(running, now, poll, _exeDir, exe, 6);   // again at Minimal: taken out
        s = k.Games.Single();
        Assert.Equal((RecorderLevel.Off, ScsKiller.SkipCrashed, false, false), (s.RecorderLevel, s.RecorderSkip, s.RecorderInstalled, s.RecorderEffective));
        Assert.Equal("Fake Game closed again shortly after starting (6 s): the recorder was taken out for this game", s.RecorderLevelReason);
        Assert.False(File.Exists(dll));
        Assert.Equal(before, NotOurs());   // only the recorder's files went
        Assert.Empty(Directory.GetFiles(_exeDir, "scskiller*"));

        // neither "record all" nor the game's own switch puts it back
        k.ReconcileRecorders();
        await k.RescanAsync(default);
        Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id));
        k.ReconcileRecorders();
        Assert.False(File.Exists(dll));
        Assert.Equal(RecorderLevel.Off, k.Games.Single().RecorderLevel);

        // Try again: back in, in full
        k.ResetRecorderHealth(_game.Id);
        s = k.Games.Single();
        Assert.Equal((RecorderLevel.Full, (string?)null, (string?)null, true), (s.RecorderLevel, s.RecorderLevelReason, s.RecorderSkip, s.RecorderInstalled));
        Assert.True(ScsKiller.IsOurProxy(dll));
        Assert.DoesNotContain("frames=0", File.ReadAllText(ini));
    }

    /// <summary>Frame generation's files (shipped, on or not) start the recorder in full like any game; an early failure
    /// steps it down as anywhere, with the frame hooks' evidence counted.</summary>
    [Fact]
    public async Task Beside_frame_generation_the_recorder_starts_in_full_and_steps_down_on_an_early_failure()
    {
        var (k, running, poll, now) = Guarded();
        foreach (var f in new[] { "sl.interposer.dll", "sl.dlss_g.dll" }) File.WriteAllBytes(Path.Combine(_exeDir, f), Planning.MiddlewarePackTests.Pe(f));
        await k.ScanAsync(default);
        var (ini, exe) = (Path.Combine(_exeDir, "scskiller.ini"), Path.GetFileName(_game.ExePath));
        var s = k.Games.Single();
        Assert.Equal(("Streamline DLSS-G", RecorderLevel.Full, true), (s.FrameGen, s.RecorderLevel, s.RecorderInstalled));
        Assert.DoesNotContain("frames=0", File.ReadAllText(ini));

        Play(running, now, poll, _exeDir, exe, 4);
        s = k.Games.Single();
        Assert.Equal((RecorderLevel.Minimal, true, true), (s.RecorderLevel, s.RecorderSteppedDown, s.RecorderInstalled));
        Assert.Equal("frame generation (Streamline DLSS-G) is present: the recorder records pipelines only, no frame times", ScsKiller.FrameGenNote(s));
        Assert.Contains("\r\nframes=0\r\n", File.ReadAllText(ini));
        Assert.Contains("SCSKiller's crash guard", File.ReadAllText(ini));
    }

    [Fact]
    public async Task Another_build_of_the_game_and_try_again_put_the_recorder_back_in_full()
    {
        var (k, running, poll, now) = Guarded();
        await k.ScanAsync(default);
        var (ini, exe) = (Path.Combine(_exeDir, "scskiller.ini"), Path.GetFileName(_game.ExePath));
        Play(running, now, poll, _exeDir, exe, 5);
        Assert.Equal(RecorderLevel.Minimal, k.Games.Single().RecorderLevel);

        k.ResetRecorderHealth(_game.Id);
        Assert.Equal(RecorderLevel.Full, k.Games.Single().RecorderLevel);
        Assert.DoesNotContain("frames=0", File.ReadAllText(ini));
        k.RefreshGame(_game.Id);   // the launch it stepped down on isn't judged again
        Assert.Equal(RecorderLevel.Full, k.Games.Single().RecorderLevel);

        Play(running, now, poll, _exeDir, exe, 5);
        Assert.Equal(RecorderLevel.Minimal, k.Games.Single().RecorderLevel);
        File.WriteAllBytes(_game.ExePath, new byte[5000]);   // a game update
        await k.RescanAsync(default);
        var s = k.Games.Single();
        Assert.Equal((RecorderLevel.Full, (string?)null, true), (s.RecorderLevel, s.RecorderLevelReason, s.RecorderInstalled));
        Assert.DoesNotContain("frames=0", File.ReadAllText(ini));
        Assert.Null(k.Store.LoadGame(_game.Id).RecorderLevelBuild);
    }

    /// <summary>The app wasn't running when the game crashed: the next start judges the launch from the recorder's files, as
    /// far as they tell: only the launch's own frames, ending early, count; creates alone never do.</summary>
    [Fact]
    public async Task A_failure_while_the_app_was_closed_is_caught_at_the_next_start()
    {
        var (k, _, _, now) = Guarded();
        await k.ScanAsync(default);
        var exe = Path.GetFileName(_game.ExePath);
        var (csv, bin) = (Path.Combine(_exeDir, "scskiller_creates.csv"), Path.Combine(_exeDir, FrameLog.FileName));
        long At(int minutes) => now().AddMinutes(minutes).ToUnixTimeMilliseconds();
        async Task<RecorderLevel> LevelAt(int minutes)   // the app's next start
        {
            var later = Managed();
            later.Clock = () => now().AddMinutes(minutes);
            await later.ScanAsync(default);
            return later.Games.Single().RecorderLevel;
        }

        // two hours, every pipeline created in its first 30 s, no frame of it logged (the file held, capped, no present hook)
        File.WriteAllText(csv, $"#session,{At(1)},{exe}\n#clock,50.0\n500.0,G,0,0,40.0\n30000.0,S,1,1,0.5\n");
        Assert.Equal(RecorderLevel.Full, await LevelAt(150));

        // its own frames end 3 s in: closed early
        File.AppendAllText(csv, $"#session,{At(160)},{exe}\n#clock,50.0\n500.0,G,0,0,40.0\n3000.0,S,1,1,0.5\n");
        File.WriteAllBytes(bin, FrameLogTests.Launch(At(160), 50_000, Enumerable.Range(1, 300).Select(i => 50.0 + i * 10)));
        Assert.Equal(RecorderLevel.Minimal, await LevelAt(170));
        Assert.Contains("\r\nframes=0\r\n", File.ReadAllText(Path.Combine(_exeDir, "scskiller.ini")));

        // at Minimal there are no frames to tell: a launch the app didn't watch, without #end, isn't judged
        File.AppendAllText(csv, $"#session,{At(180)},{exe}\n#clock,50.0\n500.0,G,0,0,40.0\n");
        Assert.Equal(RecorderLevel.Minimal, await LevelAt(190));
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, "d3d12.dll")));
    }

    /// <summary>A "Try again" while an evaluation judges a launch from the record it read before: the reset stands.</summary>
    [Fact]
    public async Task A_reset_during_an_evaluation_is_never_undone_by_its_guard()
    {
        var (k, running, poll, now) = Guarded();
        await k.ScanAsync(default);
        var exe = Path.GetFileName(_game.ExePath);
        Play(running, now, poll, _exeDir, exe, 5);
        Assert.Equal(RecorderLevel.Minimal, k.Games.Single().RecorderLevel);
        // closed early again while the app wasn't watching (the frame log was left by a Full launch's hooks)
        var at = now().AddMinutes(5).ToUnixTimeMilliseconds();
        File.AppendAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), $"#session,{at},{exe}\n#clock,50.0\n500.0,G,0,0,40.0\n3000.0,S,1,1,0.5\n");
        File.WriteAllBytes(Path.Combine(_exeDir, FrameLog.FileName), FrameLogTests.Launch(at, 50_000, Enumerable.Range(1, 300).Select(i => 50.0 + i * 10)));
        k.Clock = () => now().AddMinutes(10);

        var reset = 0;
        k.ProcessNames = () =>   // the guard asks whether the game runs after judging, outside the recorder lock
        {
            if (Interlocked.Exchange(ref reset, 1) == 0)
                Assert.True(Task.Run(() => k.ResetRecorderHealth(_game.Id)).Wait(TimeSpan.FromSeconds(30)));
            return new HashSet<string>();
        };
        k.RefreshGame(_game.Id);
        Assert.Equal(1, reset);
        var s = k.Games.Single();
        Assert.Equal((RecorderLevel.Full, false, true), (s.RecorderLevel, s.RecorderSteppedDown, s.RecorderInstalled));
        Assert.Null(k.Store.LoadGame(_game.Id).RecorderLevel);
        k.RefreshGame(_game.Id);   // the launch is from before the reset: not judged
        Assert.Equal(RecorderLevel.Full, k.Games.Single().RecorderLevel);
    }

    [Fact]
    public async Task A_recorder_from_before_the_guard_counts_only_launches_after_it_was_first_seen()
    {
        var (k, _, _, now) = Guarded();
        await k.ScanAsync(default);
        var rec = k.Store.LoadGame(_game.Id);
        rec.RecorderInstalledAt = null;   // installed by an earlier build
        k.Store.SaveGame(_game.Id, rec);
        File.WriteAllText(Path.Combine(_exeDir, "scskiller_creates.csv"),
            $"#session,{now().AddMinutes(1).ToUnixTimeMilliseconds()},{Path.GetFileName(_game.ExePath)}\n#clock,50.0\n500.0,G,0,0,40.0\n");
        var later = Managed();
        later.Clock = () => now().AddMinutes(10);
        await later.ScanAsync(default);
        Assert.Equal(RecorderLevel.Full, later.Games.Single().RecorderLevel);
        Assert.Equal(now().AddMinutes(10), later.Store.LoadGame(_game.Id).RecorderInstalledAt);
    }

    [Fact]
    public async Task An_interrupted_recorder_write_never_leaves_a_torn_dll_or_ini()
    {
        var k = Managed();
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var temp = dll + ".scskiller-new";
        k.InstallStep = step => { if (step == "temp") throw new IOException("disk full"); };
        await k.ScanAsync(default);
        Assert.False(File.Exists(dll));
        Assert.False(File.Exists(temp));
        Assert.Contains("disk full", k.Games.Single().RecorderNote);

        k.InstallStep = null;
        k.ReconcileRecorders();
        var old = File.ReadAllBytes(dll);
        Assert.Equal(File.ReadAllBytes(_proxy), old);
        Assert.Contains("mode=record", File.ReadAllText(Path.Combine(_exeDir, "scskiller.ini")));
        Assert.Empty(Directory.GetFiles(_exeDir, "*.scskiller-new"));

        // the app killed mid-copy: a half proxy under the temp name, which no game loads; the install watcher takes it for ours
        await AssertStaysArmed(k, _game);
        File.WriteAllBytes(temp, old[..8]);
        File.WriteAllText(Path.Combine(_exeDir, "scskiller.ini.scskiller-new"), "[scski");
        await AssertStaysArmed(k, _game);

        File.WriteAllBytes(_proxy, [.. "MZ newer proxy SCSKiller_StartWarm "u8, .. Guid.NewGuid().ToByteArray()]);   // an update, cut off
        var updated = Managed();
        updated.InstallStep = step => { if (step == "temp") throw new IOException("disk full"); };
        await updated.ScanAsync(default);
        Assert.Equal(old, File.ReadAllBytes(dll));   // the old recorder, whole
        Assert.False(File.Exists(temp));
        Assert.Contains("couldn't update: disk full", updated.Games.Single().RecorderNote);

        updated.SetRecorderOverride(_game.Id, RecorderOverride.Off);   // the removal takes the temp names too, whatever they hold
        Assert.False(File.Exists(dll));
        Assert.Empty(Directory.GetFiles(_exeDir, "*.scskiller-new"));
    }

    /// <summary>The recorder's own marks in the csv: a removed device (#removed, upstream issue 62) fails its launch whatever
    /// its length and its #end; a CreateSwapChain called back into (#frames_off reentry, upstream issue 48) and the hooks the
    /// launch ran (#hooks) are told.</summary>
    [Fact]
    public void A_removed_device_fails_its_launch_and_the_csv_tells_the_hooks_it_ran()
    {
        var csv = Path.Combine(_root, "creates.csv");
        Directory.CreateDirectory(_root);
        var start = DateTimeOffset.FromUnixTimeMilliseconds(T0 + 700_000);
        RecorderVerdict? Judge(PlayWindow? played = null) => RecorderHealth.Judge(csv, "Fake.exe", T0 - 60_000, 0, played, null);
        File.WriteAllText(csv, $"#session,{T0 - 600_000},Fake.exe\n#clock,90.0\n#hooks,1,1\n1000.0,G,0,0,50.0\n#removed,{T0 - 500_000},0x887a0006\n#end,{T0 - 300_000},90000.0\n"
            + $"#session,{T0},Fake.exe\n#clock,100.0\n#hooks,1,0\n1000.0,G,0,0,50.0\n#end,{T0 + 600_000},600100.0\n");
        Assert.Equal(new RecorderVerdict(T0, null, HooksOn: true), Judge());   // an earlier launch's removal isn't this one's

        File.AppendAllText(csv, $"#session,{T0 + 700_000},Fake.exe\n#clock,100.0\n#hooks,0,0\n1000.0,G,0,0,50.0\n#removed,{T0 + 1_000_000},0x887a0006\n"
            + $"#removed,{T0 + 1_000_500},0x887a0005\n#end,{T0 + 1_001_000},301100.0\n");
        var v = Judge()!;
        Assert.Equal(new DeviceRemoved(TimeSpan.FromMinutes(5), "0x887a0006"), v.Removed);   // the first, five minutes in: a failure though it ended
        Assert.Equal((true, (TimeSpan?)null, (bool?)false, false), (v.Failed, v.EarlyFailure, v.HooksOn, v.Reentry));
        Assert.Equal(v, Judge(new(start.AddSeconds(-2), start.AddMinutes(10))));   // a long watched run too

        File.AppendAllText(csv, $"#session,{T0 + 2_000_000},Fake.exe\n#clock,100.0\n1000.0,G,0,0,50.0\n#frames_off,{T0 + 2_010_000},10100.0,reentry\n");
        v = Judge()!;
        Assert.Equal((true, false, (bool?)null), (v.Reentry, v.Failed, v.HooksOn));   // an older proxy writes no #hooks
        Assert.Equal(10100.0, SessionLog.Launches(csv).Last().FramesOffT);
        File.AppendAllText(csv, $"#session,{T0 + 3_000_000},Fake.exe\n#clock,100.0\n1000.0,G,0,0,50.0\n#frames_off,{T0 + 3_010_000},10100.0\n");
        Assert.False(Judge()!.Reentry);   // frame generation's swap chain

        Assert.Equal("The graphics driver crashed while Fake Game was recording (300 s in, device removed 0x887a0006): it now records pipelines only for this game (no frame times)",
            RecorderHealth.RemovedNote("Fake Game", RecorderLevel.Minimal, new(TimeSpan.FromMinutes(5), "0x887a0006")));
        Assert.EndsWith("device removed 0x887a0005): the recorder was taken out for this game",
            RecorderHealth.RemovedNote("Fake Game", RecorderLevel.Off, new(TimeSpan.FromMinutes(5), "0x887a0005")));
    }

    /// <summary>A driver crash while recording (#removed) steps the recorder down after a launch of any length, Full ->
    /// pipelines only -> out, and the game page says the driver crashed.</summary>
    [Fact]
    public async Task A_driver_crash_while_recording_steps_the_recorder_down_whatever_the_launch_lasted()
    {
        var (k, running, poll, now) = Guarded();
        await k.ScanAsync(default);
        var (dll, ini, exe) = (Path.Combine(_exeDir, "d3d12.dll"), Path.Combine(_exeDir, "scskiller.ini"), Path.GetFileName(_game.ExePath));
        Play(running, now, poll, _exeDir, exe, 300, end: true, marks: at => $"#hooks,1,1\n#removed,{at + 290_000},0x887a0006\n");
        var s = k.Games.Single();
        Assert.Equal((RecorderLevel.Minimal, true, true), (s.RecorderLevel, s.RecorderSteppedDown, s.RecorderInstalled));
        Assert.Equal("The graphics driver crashed while Fake Game was recording (290 s in, device removed 0x887a0006): it now records pipelines only for this game (no frame times)",
            s.RecorderLevelReason);
        Assert.Contains("\r\nframes=0\r\nnvapi=0\r\n", File.ReadAllText(ini));
        Assert.Contains(s.RecorderLevelReason!, File.ReadAllText(Path.Combine(k.Store.DataDir, "recorders.log")));

        Play(running, now, poll, _exeDir, exe, 600, marks: at => $"#hooks,0,0\n#removed,{at + 500_000},0x887a0005\n");   // at pipelines only: out
        s = k.Games.Single();
        Assert.Equal((RecorderLevel.Off, ScsKiller.SkipCrashed, false), (s.RecorderLevel, s.RecorderSkip, s.RecorderInstalled));
        Assert.EndsWith("(500 s in, device removed 0x887a0005): the recorder was taken out for this game", s.RecorderLevelReason);
        Assert.False(File.Exists(dll));
    }

    /// <summary>At pipelines only, an early close of a launch that still ran the hooks (#hooks: it started before Reconcile
    /// wrote frames=0) is no failure of that level while the ini is ours, which takes it from the next launch; the user's own
    /// ini can't take it: the recorder goes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_launch_that_still_ran_the_hooks_never_fails_pipelines_only(bool userIni)
    {
        var (k, running, poll, now) = Guarded();
        await k.ScanAsync(default);
        var (ini, exe) = (Path.Combine(_exeDir, "scskiller.ini"), Path.GetFileName(_game.ExePath));
        Play(running, now, poll, _exeDir, exe, 5, marks: _ => "#hooks,1,1\n");
        Assert.Equal(RecorderLevel.Minimal, k.Games.Single().RecorderLevel);
        if (userIni) File.WriteAllText(ini, "[scskiller]\r\nmode=record\r\n");

        Play(running, now, poll, _exeDir, exe, 5, marks: _ => "#hooks,1,1\n");
        Assert.Equal(userIni ? RecorderLevel.Off : RecorderLevel.Minimal, k.Games.Single().RecorderLevel);
        if (userIni) return;
        Assert.Contains("Fake Game closed early with the recorder's hooks still in", File.ReadAllText(Path.Combine(k.Store.DataDir, "recorders.log")));
        Play(running, now, poll, _exeDir, exe, 5, marks: _ => "#hooks,0,0\n");   // at pipelines only: out
        Assert.Equal(RecorderLevel.Off, k.Games.Single().RecorderLevel);
    }

    /// <summary>Another hook that called the recorder's CreateSwapChain back (#frames_off reentry, upstream issue 48) turns
    /// frame timing off for the game (frames=0, the NVAPI hooks stay) until "Try again" or another build of the game.</summary>
    [Fact]
    public async Task A_swap_chain_hook_called_back_turns_frame_timing_off_for_the_game()
    {
        var (k, running, poll, now) = Guarded();
        await k.ScanAsync(default);
        var (ini, exe) = (Path.Combine(_exeDir, "scskiller.ini"), Path.GetFileName(_game.ExePath));
        string Reentry(long at) => $"#hooks,1,1\n#frames_off,{at + 9_800},9800.0,reentry\n";
        Play(running, now, poll, _exeDir, exe, 300, end: true, marks: Reentry);
        var s = k.Games.Single();
        Assert.Equal((RecorderLevel.Full, RecorderHealth.ReentryNote("Fake Game"), true), (s.RecorderLevel, s.RecorderFramesOff, s.RecorderInstalled));
        Assert.Contains("\r\nframes=0\r\n", File.ReadAllText(ini));
        Assert.DoesNotContain("nvapi=0", File.ReadAllText(ini));
        Assert.Contains(s.RecorderFramesOff!, File.ReadAllText(Path.Combine(k.Store.DataDir, "recorders.log")));

        k.ResetRecorderHealth(_game.Id);
        Assert.Null(k.Games.Single().RecorderFramesOff);
        Assert.DoesNotContain("frames=0", File.ReadAllText(ini));

        Play(running, now, poll, _exeDir, exe, 300, end: true, marks: Reentry);
        Assert.NotNull(k.Games.Single().RecorderFramesOff);
        File.WriteAllBytes(_game.ExePath, new byte[5000]);   // a game update
        await k.RescanAsync(default);
        Assert.Null(k.Games.Single().RecorderFramesOff);
        Assert.DoesNotContain("frames=0", File.ReadAllText(ini));
    }

    /// <summary>An evaluation that read the record before the exit's watched run was saved judges without it: its verdict
    /// isn't saved, and the exit's own evaluation judges the launch.</summary>
    [Fact]
    public async Task A_verdict_judged_without_the_exits_watched_run_is_left_to_the_exits_evaluation()
    {
        var (k, _, _, now) = Guarded();
        await k.ScanAsync(default);
        var exe = Path.GetFileName(_game.ExePath);
        var start = now().AddMinutes(1);
        File.AppendAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), $"#session,{start.ToUnixTimeMilliseconds()},{exe}\n#clock,50.0\n500.0,G,0,0,40.0\n");
        k.Clock = () => now().AddMinutes(5);
        var saved = 0;
        k.ProcessNames = () =>   // the guard asks whether the game runs after judging: the exit's poll saves its run meanwhile
        {
            if (Interlocked.Exchange(ref saved, 1) == 0)
            {
                var rec = k.Store.LoadGame(_game.Id);
                rec.LastPlay = new PlayWindow(start.AddSeconds(-2), start.AddSeconds(4));
                k.Store.SaveGame(_game.Id, rec);
            }
            return new HashSet<string>();
        };
        k.RefreshGame(_game.Id);
        Assert.Equal(1, saved);
        Assert.Null(k.Store.LoadGame(_game.Id).RecorderSessionSeen);
        Assert.Equal(RecorderLevel.Full, k.Games.Single().RecorderLevel);
        k.RefreshGame(_game.Id);   // the exit's evaluation
        Assert.Equal(RecorderLevel.Minimal, k.Games.Single().RecorderLevel);
    }

    /// <summary>OptiScaler as the game's dxgi.dll (upstream issue 69: Onimusha: Way of the Sword crashed seconds in): the
    /// recorder starts at pipelines only, and the crash guard takes it out after an early close there.</summary>
    [Fact]
    public async Task With_optiscaler_as_dxgi_dll_the_recorder_starts_at_pipelines_only()
    {
        var dxgi = Path.Combine(_exeDir, "dxgi.dll");
        File.WriteAllBytes(dxgi, Planning.MiddlewarePackTests.Pe("OptiScaler.dll"));
        Assert.True(Core.Games.FrameGen.OptiScalerDxgi(_exeDir));
        var (k, running, poll, now) = Guarded();
        await k.ScanAsync(default);
        var (ini, exe) = (Path.Combine(_exeDir, "scskiller.ini"), Path.GetFileName(_game.ExePath));
        var s = k.Games.Single();
        Assert.Equal((RecorderLevel.Minimal, false, ScsKiller.OptiScalerStartNote, true), (s.RecorderLevel, s.RecorderSteppedDown, s.RecorderLevelReason, s.RecorderInstalled));
        Assert.Contains("; OptiScaler is the game's dxgi.dll: no frame-timing or NVAPI hooks, pipelines only\r\nframes=0\r\nnvapi=0\r\n", File.ReadAllText(ini));

        Play(running, now, poll, _exeDir, exe, 5, marks: _ => "#hooks,0,0\n");
        s = k.Games.Single();
        Assert.Equal((RecorderLevel.Off, ScsKiller.SkipCrashed), (s.RecorderLevel, s.RecorderSkip));
        Assert.StartsWith("Fake Game closed shortly after starting (", s.RecorderLevelReason);   // not "again": it started there

        File.WriteAllBytes(dxgi, Planning.MiddlewarePackTests.Pe("dxgi.dll"));   // another dxgi.dll
        Assert.False(Core.Games.FrameGen.OptiScalerDxgi(_exeDir));
        Assert.Equal(RecorderLevel.Full, ScsKiller.StartLevel(null, false));
        Assert.Equal(RecorderLevel.Minimal, ScsKiller.StartLevel(null, false, optiScalerDxgi: true));
    }
}
