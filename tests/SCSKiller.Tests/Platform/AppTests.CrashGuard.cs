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
        RecorderVerdict? Judge(PlayWindow? played = null, FrameReport? frames = null, bool framesExpected = false, long after = T0 - 60_000, long seen = 0,
            string exe = "Fake.exe") => RecorderHealth.Judge(csv, exe, after, seen, played, frames, framesExpected);
        File.WriteAllText(csv, $"#session,{T0 - 600_000},Fake.exe\n#clock,90.0\n1000.0,G,0,0,50.0\n#end,{T0 - 300_000},90000.0\n"   // an older launch
            + $"#session,{T0},Fake.exe\n#clock,100.0\n1000.0,G,0,0,50.0\n4000.0,S,1,1,0.5\n");

        Assert.Equal(new RecorderVerdict(T0, TimeSpan.FromSeconds(4)), Judge(Ran(4)));         // watched: it exited 4 s after starting
        Assert.Equal(new RecorderVerdict(T0, null), Judge(Ran(120)));                          // played two minutes
        Assert.Equal(new RecorderVerdict(T0, null), Judge());                                  // no watched run, no frame log expected: can't tell
        Assert.Equal(new RecorderVerdict(T0, null), Judge(new(start.AddHours(-2), start.AddHours(-1))));   // an older run's window
        Assert.Equal(TimeSpan.FromSeconds(4), Judge(framesExpected: true)!.EarlyFailure);     // the frame hooks on, no frame of this launch
        Assert.Equal(TimeSpan.FromSeconds(4), Judge(frames: FramesOf(T0, 3))!.EarlyFailure);   // its frames end within it
        Assert.Null(Judge(frames: FramesOf(T0, 600))!.EarlyFailure);                          // ten minutes of frames
        Assert.Equal(TimeSpan.FromSeconds(4), Judge(frames: FramesOf(T0 - 600_000, 600), framesExpected: true)!.EarlyFailure);   // another launch's frames
        Assert.Null(Judge(frames: FramesOf(T0 - 600_000, 3))!.EarlyFailure);                 // ...and the hooks were off: can't tell
        Assert.Null(Judge(Ran(4), seen: T0));                                                  // judged once: never again
        Assert.Null(Judge(Ran(4), after: T0));                                                 // from before the recorder's install or level
        Assert.Null(Judge(Ran(4), exe: "Other.exe"));                                          // only another exe's launches

        File.AppendAllText(csv, $"#end,{T0 + 4_000},4100.0\n");
        Assert.Equal(new RecorderVerdict(T0, null), Judge(Ran(4), framesExpected: true));     // it exited cleanly: never a failure

        File.WriteAllText(csv, $"#session,{T0},Fake.exe\n#clock,100.0\n");                     // no create at all
        Assert.Equal(new RecorderVerdict(T0, null), Judge(framesExpected: true));
        Assert.Equal(TimeSpan.FromSeconds(4), Judge(Ran(4))!.EarlyFailure);                   // but the watched run tells
        File.WriteAllText(csv, "1000.0,G,0,0,50.0\n");                                          // an older proxy: no #session, no start
        Assert.Null(Judge(Ran(4), framesExpected: true));
        File.Delete(csv);
        Assert.Null(Judge(Ran(4)));

        Assert.Equal("Fake Game closed 4 s after starting with the recorder: it now records pipelines only for this game (no frame times)",
            RecorderHealth.Note("Fake Game", RecorderLevel.Minimal, TimeSpan.FromSeconds(4.2)));
        Assert.Equal("Fake Game closed again shortly after starting (3 s): the recorder was taken out for this game",
            RecorderHealth.Note("Fake Game", RecorderLevel.Off, TimeSpan.FromSeconds(3)));
    }

    /// <summary>The game watched from start to exit: a run of <paramref name="seconds"/> whose launch the recorder marked
    /// (<paramref name="end"/>: with its #end).</summary>
    static void Play(HashSet<string> running, Func<DateTimeOffset> now, Action<int> poll, string exeDir, string exe, int seconds, bool end = false)
    {
        poll(3);
        running.Add(exe);
        poll(3);
        var start = now().ToUnixTimeMilliseconds();
        File.AppendAllText(Path.Combine(exeDir, "scskiller_creates.csv"), $"#session,{start},{exe}\n#clock,50.0\n500.0,G,0,0,40.0\n{seconds * 900.0:0.0},S,1,1,0.5\n"
            + (end ? $"#end,{start + seconds * 1000L},{seconds * 1000.0:0.0}\n" : ""));
        for (var t = 0; t < seconds; t += 3) poll(3);
        running.Clear();
        for (int i = 0; i < ScsKiller.ExitPolls; i++) poll(3);
    }

    (ScsKiller K, HashSet<string> Running, Action<int> Poll, Func<DateTimeOffset> Now) Guarded()
    {
        var k = Managed();
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
        using var _ = new FreshLedger(_root);
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

    [Fact]
    public async Task Another_build_of_the_game_and_try_again_put_the_recorder_back_in_full()
    {
        using var _ = new FreshLedger(_root);
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
    /// far as they tell (the frame hooks were on, and no frame of that launch was logged).</summary>
    [Fact]
    public async Task A_failure_while_the_app_was_closed_is_caught_at_the_next_start()
    {
        using var _ = new FreshLedger(_root);
        var (k, _, _, now) = Guarded();
        await k.ScanAsync(default);
        var installed = k.Store.LoadGame(_game.Id).RecorderInstalledAt!.Value.ToUnixTimeMilliseconds();
        var csv = Path.Combine(_exeDir, "scskiller_creates.csv");
        File.WriteAllText(csv, $"#session,{installed + 60_000},{Path.GetFileName(_game.ExePath)}\n#clock,50.0\n500.0,G,0,0,40.0\n3000.0,S,1,1,0.5\n");

        var later = Managed();
        later.Clock = () => now().AddMinutes(10);
        await later.ScanAsync(default);
        Assert.Equal(RecorderLevel.Minimal, later.Games.Single().RecorderLevel);
        Assert.Contains("\r\nframes=0\r\n", File.ReadAllText(Path.Combine(_exeDir, "scskiller.ini")));

        // at Minimal there are no frames to tell: a launch the app didn't watch, without #end, isn't judged
        File.AppendAllText(csv, $"#session,{now().AddMinutes(20).ToUnixTimeMilliseconds()},{Path.GetFileName(_game.ExePath)}\n#clock,50.0\n500.0,G,0,0,40.0\n");
        var third = Managed();
        third.Clock = () => now().AddMinutes(30);
        await third.ScanAsync(default);
        Assert.Equal(RecorderLevel.Minimal, third.Games.Single().RecorderLevel);
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, "d3d12.dll")));
    }

    [Fact]
    public async Task A_recorder_from_before_the_guard_counts_only_launches_after_it_was_first_seen()
    {
        using var _ = new FreshLedger(_root);
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
        using var _ = new FreshLedger(_root);
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
}
