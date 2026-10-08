namespace SCSKiller.Core.App;

/// <summary>The last launch judged by the crash guard: its <c>#session</c> stamp (unix ms), and how long it lasted when it
/// closed early (null: it didn't, or the data can't tell).</summary>
public sealed record RecorderVerdict(long Session, TimeSpan? EarlyFailure);

/// <summary>The crash guard (ARCHITECTURE.md, Recorder): a recorded launch that closed early with the recorder in steps it
/// down for the game. Judged from what the recorder already writes (scskiller_creates.csv's <c>#session</c> and <c>#end</c>,
/// the frame log) and the run the app watched (<see cref="GameRecord.LastPlay"/>). An early failure has no <c>#end</c>
/// (the recorder writes it as its process detaches; a crash never gets there) and lasted under
/// <see cref="Threshold"/>. Some engines end their own process without <c>#end</c> (Unreal), and a game may create all its
/// pipelines in its first seconds, so a launch is never judged from its creates alone: it needs the watched run, or its own
/// frame log ending within the threshold. A frame log without the launch tells nothing (held, capped, the present hook not
/// in), nor one frame generation's swap chain cut short (<c>#frames_off</c>).</summary>
public static class RecorderHealth
{
    /// <summary>FINAL FANTASY XVI with DLSS-G and the frame hooks crashed 4 s after launch; a game quit on purpose before
    /// its menu is rare under this.</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(45);

    /// <summary>The csv's last launch of <paramref name="exeFileName"/> (null: any exe's), if it started after
    /// <paramref name="after"/> (unix ms: the install or the level's start) and after <paramref name="seen"/> (the last one
    /// judged); null = nothing new to judge. It closed early when it has no <c>#end</c> and either the watched run
    /// <paramref name="played"/> holds its start and ended within the threshold of it, or (no watched run) the frame log
    /// <paramref name="frames"/> is of this launch and its last frame and last create are within the threshold. Anything
    /// else, a launch without creates or without its frames (and no watched run), is judged fine.</summary>
    public static RecorderVerdict? Judge(string csvPath, string? exeFileName, long after, long seen, PlayWindow? played, FrameReport? frames,
        TimeSpan? threshold = null) =>
        Judge(SessionLog.Launches(csvPath), exeFileName, after, seen, played, frames, threshold ?? Threshold);

    internal static RecorderVerdict? Judge(IEnumerable<SessionLog.CsvLaunch> launches, string? exeFileName, long after, long seen, PlayWindow? played,
        FrameReport? frames, TimeSpan threshold)
    {
        var last = launches.LastOrDefault(l => l.Start is > 0 && !SessionLog.OtherExe(l.Exe, exeFileName));
        if (last?.Start is not long start || start <= after || start <= seen) return null;
        if (last.End != null) return new(start, null);
        if (played is { } p && p.From.ToUnixTimeMilliseconds() <= start && start <= p.To.ToUnixTimeMilliseconds())
        {
            var ran = TimeSpan.FromMilliseconds(p.To.ToUnixTimeMilliseconds() - start);
            return new(start, ran < threshold ? ran : null);
        }
        // frame generation's swap chain ended the frame log, not the launch (#frames_off)
        if (last.Creates.Count == 0 || frames == null || frames.LaunchUnixMs != start || last.FramesOffT != null) return new(start, null);
        // both on the recorder's clock, from its load: a little longer than from the #session
        var lasted = TimeSpan.FromMilliseconds(Math.Max(last.Creates[^1].T, frames.Duration.TotalMilliseconds));
        return new(start, lasted < threshold ? lasted : null);
    }

    /// <summary>The note and log line for a step down to <paramref name="level"/> after a launch of <paramref name="lasted"/>;
    /// <paramref name="again"/>: from a step down (to Off from one that started at Minimal: not).</summary>
    public static string Note(string game, RecorderLevel level, TimeSpan lasted, bool again = true) => level == RecorderLevel.Off
        ? $"{game} closed {(again ? "again " : "")}shortly after starting ({Seconds(lasted)}): the recorder was taken out for this game"
        : $"{game} closed {Seconds(lasted)} after starting with the recorder: it now records pipelines only for this game (no frame times)";

    static string Seconds(TimeSpan t) => $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))} s";
}
