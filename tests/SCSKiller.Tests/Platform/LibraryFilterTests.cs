using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

/// <summary>The Library's "Hide unsupported games": Unsupported only, a view over the states (nothing dropped from them).</summary>
public class LibraryFilterTests
{
    static GameState S(string id, GameStatus status, AntiCheat antiCheat = AntiCheat.None) =>
        new(new(id, id, Store.Steam, @"X:\none", @"X:\none\game.exe"), null, antiCheat, status, "", null, null, null, null, null, null, null, false, null);

    static readonly GameState[] Games =
    [
        S("steam:1", GameStatus.Warmed), S("steam:2", GameStatus.Ready), S("steam:3", GameStatus.Unsupported),
        S("steam:4", GameStatus.NeedsRecording), S("steam:5", GameStatus.Stale), S("steam:6", GameStatus.Unsupported, AntiCheat.EasyAntiCheat),
        S("steam:7", GameStatus.Ready, AntiCheat.BattlEye),   // anti-cheat but compiles: listed
    ];

    static string[] Ids(IEnumerable<GameState> g) => g.Select(s => s.Game.Id).ToArray();

    [Fact]
    public void Off_by_default_and_then_lists_everything()
    {
        Assert.False(AppStore.DefaultSettings.HideUnsupported);
        var (shown, hidden) = LibraryFilter.Apply(Games, s => s, false);
        Assert.Equal(Ids(Games), Ids(shown));
        Assert.Equal(0, hidden);
        Assert.All(Games, s => Assert.False(LibraryFilter.Hides(s, false)));
    }

    [Fact]
    public void On_hides_only_unsupported_games_in_order_and_counts_them()
    {
        var (shown, hidden) = LibraryFilter.Apply(Games, s => s, true);
        Assert.Equal(["steam:1", "steam:2", "steam:4", "steam:5", "steam:7"], Ids(shown));
        Assert.Equal(2, hidden);
        Assert.Equal([GameStatus.Unsupported], Enum.GetValues<GameStatus>().Where(st => LibraryFilter.Hides(S("x", st), true)));
    }

    [Fact]
    public void A_hidden_game_shows_again_once_its_status_changes()
    {
        var encrypted = S("steam:3", GameStatus.Unsupported);
        Assert.True(LibraryFilter.Hides(encrypted, true));
        var unlocked = encrypted with { Status = GameStatus.Ready };   // its key found: the next scan makes it Ready
        var games = Games.Select(s => s.Game.Id == unlocked.Game.Id ? unlocked : s).ToList();
        var (shown, hidden) = LibraryFilter.Apply(games, s => s, true);
        Assert.Contains("steam:3", Ids(shown));
        Assert.Equal(1, hidden);
    }

    [Fact]
    public void Apply_works_on_any_row_type_through_its_state()
    {
        var rows = Games.Select((s, i) => (Index: i, State: s)).ToList();
        var (shown, hidden) = LibraryFilter.Apply(rows, r => r.State, true);
        Assert.Equal([0, 1, 3, 4, 6], shown.Select(r => r.Index));
        Assert.Equal(2, hidden);
        Assert.Empty(LibraryFilter.Apply(Games.Where(s => s.Status == GameStatus.Unsupported), s => s, true).Shown);
    }
}
