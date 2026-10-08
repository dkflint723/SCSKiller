using System.Collections.Concurrent;
using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

// Upstream issue 51, a first scan that showed "Looking for games…" for hours: each game shows as it's read, the scan says
// which game it reads, and one game whose read outlasts ScsKiller.ReadBudget no longer holds up the others.
public partial class AppTests
{
    /// <summary>An Unreal game per id; the game <paramref name="slow"/> names waits in Detect until <see cref="Release"/>.</summary>
    sealed class SlowReader(string slow) : IEngineReader
    {
        public readonly ManualResetEventSlim Entered = new(), Release = new();
        public EngineInfo? Detect(Game game)
        {
            if (game.Id != slow) return Unreal;
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(30));
            return Unreal;
        }
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) => new("content-1", ["PCD3D_SM6"], new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    [Fact]
    public async Task A_scan_lists_each_game_as_it_is_read_and_says_which_it_reads()
    {
        Game[] games = [FakeGame("test:a", "Alpha"), FakeGame("test:b", "Bravo"), FakeGame("test:c", "Charlie")];
        var reader = new SlowReader("test:b");
        var k = Killer(reader, games: games);
        var steps = new ConcurrentQueue<string?>();
        var changed = new ConcurrentQueue<string>();
        k.ScanProgress += steps.Enqueue;
        k.GameChanged += s => changed.Enqueue(s.Game.Id);
        var scan = k.ScanAsync(default);
        Assert.True(reader.Entered.Wait(TimeSpan.FromSeconds(10)));
        await Until(() => k.Games.Any(s => s.Game.Id == "test:a"));   // read before Bravo: listed while Bravo is still read
        Assert.Equal(["test:a"], k.Games.Select(s => s.Game.Id));
        Assert.Equal(["test:a"], changed);
        Assert.Equal(["Reading Alpha (1 of 3)", "Reading Bravo (2 of 3)"], steps);
        reader.Release.Set();
        await scan;
        Assert.Equal(["test:a", "test:b", "test:c"], k.Games.Select(s => s.Game.Id).Order());
        Assert.Equal(["Reading Alpha (1 of 3)", "Reading Bravo (2 of 3)", "Reading Charlie (3 of 3)", null], steps);

        // the next scan shows the games it hasn't read yet as they were: none vanishes meanwhile
        reader.Release.Reset();
        reader.Entered.Reset();
        scan = k.RescanAsync(default);
        Assert.True(reader.Entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(["test:a", "test:b", "test:c"], k.Games.Select(s => s.Game.Id).Order());
        reader.Release.Set();
        await scan;
    }

    [Fact]
    public async Task A_game_whose_read_outlasts_the_budget_is_shown_once_read_and_the_scan_goes_on()
    {
        Game[] games = [FakeGame("test:a", "Alpha"), FakeGame("test:b", "Bravo"), FakeGame("test:c", "Charlie")];
        var reader = new SlowReader("test:b");
        var k = Killer(reader, games: games);
        var log = new List<string>();
        k.Log = new Lines(log);
        k.ReadBudget = TimeSpan.FromMilliseconds(300);
        var states = await k.ScanAsync(default);   // returns with Bravo still being read
        var bravo = states.Single(s => s.Game.Id == "test:b");
        Assert.Equal((GameStatus.Unsupported, "still reading its files after 1 s; it shows here once they're read"), (bravo.Status, bravo.StatusReason));
        Assert.NotEqual(GameStatus.Unsupported, states.Single(s => s.Game.Id == "test:c").Status);   // read after it
        lock (log) Assert.Contains("Bravo: still reading its files after 1 s: the scan goes on, and the game shows once they're read", log);

        var changed = new TaskCompletionSource<GameState>(TaskCreationOptions.RunContinuationsAsynchronously);
        k.GameChanged += s => { if (s.Game.Id == "test:b") changed.TrySetResult(s); };
        reader.Release.Set();
        var read = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Unreal.Family, read.Engine?.Family);
        Assert.NotEqual(GameStatus.Unsupported, read.Status);
        Assert.Equal(read, k.Games.Single(s => s.Game.Id == "test:b"));
        lock (log) Assert.Contains(log, l => l.StartsWith("Bravo: read in ", StringComparison.Ordinal) && l.EndsWith(", after its scan went on", StringComparison.Ordinal));
    }

    /// <summary><see cref="FakeReader"/>'s Unreal game; a held game's Detect waits for its gate's Release (open until <see cref="Hold"/>).</summary>
    sealed class HeldReader(params string[] held) : IEngineReader
    {
        readonly FakeReader inner = new(Unreal);
        public readonly Dictionary<string, (ManualResetEventSlim Entered, ManualResetEventSlim Release)> Gates =
            held.ToDictionary(id => id, _ => (new ManualResetEventSlim(), new ManualResetEventSlim(true)));
        public readonly ConcurrentDictionary<string, int> Detects = new();
        public void Hold()
        {
            foreach (var (entered, release) in Gates.Values)
            {
                entered.Reset();
                release.Reset();
            }
        }
        public EngineInfo? Detect(Game game)
        {
            Detects.AddOrUpdate(game.Id, 1, (_, n) => n + 1);
            if (Gates.TryGetValue(game.Id, out var gate)) { gate.Entered.Set(); gate.Release.Wait(TimeSpan.FromSeconds(30)); }
            return inner.Detect(game);
        }
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) => inner.Index(game, e, log, ct);
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) => inner.ReadShaders(game, e, sha1s, sink, ct);
    }

    static bool StillReading(GameState s) => s.StatusReason.StartsWith(ScsKiller.StillReading, StringComparison.Ordinal);

    /// <summary>With "Scan games when SCSKiller starts" off and two games read again, both slow: the first is listed still
    /// reading before the second is read, so its read is stored as soon as it ends, and its recorder is never acted on from
    /// the placeholder. A scan while a read still runs shows the game still reading without reading it again, and that read
    /// is stored under the later scan.</summary>
    [Fact]
    public async Task On_a_kept_list_a_late_read_is_stored_as_it_ends_and_leaves_the_recorder()
    {
        var a = _game with { Store = Store.Steam, Version = "100" };
        var z = FakeGame("test:z", "Zulu") with { Store = Store.Steam, Version = "100" };
        var reader = new HeldReader(a.Id, z.Id);
        var first = Killer(reader, sources: [new FakeSource([a, z], Store.Steam)]);
        first.Settings = first.Settings with { ScanAtStart = false };
        first.ProcessNames = () => new HashSet<string>();
        await first.ScanAsync(default, userRequested: true);
        first.InstallRecorder(a.Id);
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        Assert.True(File.Exists(dll));

        reader.Hold();
        foreach (var exe in new[] { a.ExePath, z.ExePath }) File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddMinutes(1));   // detected again
        var k = Killer(reader, sources: [new FakeSource([a with { Version = "101" }, z with { Version = "101" }], Store.Steam)]);   // a restart, both updated since
        k.ManageRecorders = true;
        k.ProcessNames = () => new HashSet<string>();
        k.ReadBudget = TimeSpan.FromMilliseconds(300);
        var scan = k.ScanAsync(default);
        Assert.True(reader.Gates[z.Id].Entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(StillReading(k.Games.Single(s => s.Game.Id == a.Id)));   // listed before Zulu is read
        Assert.True(k.Scanning);   // the app's notified and auto-queued stores wait for the whole list

        // reconciled meanwhile, as a game's exit or the window's activation would: the placeholder is never the game's state
        var reconciling = Task.Run(async () =>
        {
            while (StillReading(k.Games.Single(s => s.Game.Id == a.Id)))
            {
                k.ReconcileRecorders(a.Id);
                await Task.Delay(5);
            }
        });
        reader.Gates[a.Id].Release.Set();
        await reconciling.WaitAsync(TimeSpan.FromSeconds(10));
        var read = k.Games.Single(s => s.Game.Id == a.Id);
        Assert.NotEqual(GameStatus.Unsupported, read.Status);
        Assert.True(read.RecorderInstalled && File.Exists(dll));

        Assert.True(StillReading((await scan).Single(s => s.Game.Id == z.Id)));
        Assert.False(k.Scanning);
        var detects = reader.Detects[z.Id];
        var again = await k.RescanAsync(default);   // Zulu's read still runs: not started again
        Assert.True(StillReading(again.Single(s => s.Game.Id == z.Id)));
        Assert.Equal(detects, reader.Detects[z.Id]);
        reader.Gates[z.Id].Release.Set();
        await Until(() => !StillReading(k.Games.Single(s => s.Game.Id == z.Id)));
        Assert.NotEqual(GameStatus.Unsupported, k.Games.Single(s => s.Game.Id == z.Id).Status);
        Assert.True(File.Exists(dll));
        await Until(() => k.Games.Single(s => s.Game.Id == z.Id).RecorderInstalled);   // reconciled once read, as its scan would have
        await k.RecorderIndexing.WaitAsync(TimeSpan.FromSeconds(10));   // its index for the recorder holds compile.lock
    }

    /// <summary>A read stored after its scan went on gets that scan's plan check (Settings' "check plans when idle").</summary>
    [Fact]
    public async Task A_late_read_gets_the_plan_check_its_scan_went_on_without()
    {
        var reader = new HeldReader(_game.Id);
        var k = Killer(reader, new FakePlanner(records: [new('B', [.. new byte[20], 9]), new('P', [1]), new('P', [2]), new('C', [3])]), new FakeWarmer());
        k.IdleTime = () => TimeSpan.FromHours(1);   // plan checks are "when idle" items
        await k.ScanAsync(default);
        await Compile(k);
        OlderPlanner(k);
        reader.Hold();
        k.CheckPlans = true;
        k.ReadBudget = TimeSpan.FromMilliseconds(300);
        Assert.True(StillReading((await k.RescanAsync(default)).Single()));
        Assert.DoesNotContain(k.Queue, q => q.PlanCheck);
        reader.Gates[_game.Id].Release.Set();
        await Until(() => k.Queue.Any(q => q.PlanCheck));
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
