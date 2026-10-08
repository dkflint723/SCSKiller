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
}
