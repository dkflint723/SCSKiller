namespace SCSKiller.Core.App;

/// <summary>The Library's "Hide unsupported games" (<see cref="Settings.HideUnsupported"/>): only <see cref="GameStatus.Unsupported"/>
/// games are left out, so an anti-cheat game that compiles stays. A view only: hidden games are still scanned, keep their
/// state and show again once their status changes (an encrypted game unlocked by its key).</summary>
public static class LibraryFilter
{
    public static bool Hides(GameState s, bool hideUnsupported) => hideUnsupported && s.Status == GameStatus.Unsupported;

    /// <summary>The items to list, in their order, and how many were left out.</summary>
    public static (List<T> Shown, int Hidden) Apply<T>(IEnumerable<T> items, Func<T, GameState> state, bool hideUnsupported)
    {
        var shown = new List<T>();
        int hidden = 0;
        foreach (var item in items)
            if (Hides(state(item), hideUnsupported)) hidden++;
            else shown.Add(item);
        return (shown, hidden);
    }
}
