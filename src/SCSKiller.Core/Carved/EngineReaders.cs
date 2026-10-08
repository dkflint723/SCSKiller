namespace SCSKiller.Core.Carved;

/// <summary>Several engine readers as one, tried in order. Detect: the first result that can be indexed, else the first
/// reader's that recognized the game (its reason is the most specific, e.g. Unreal's "encrypted" over the carver's "packed").
/// A reader that throws is skipped; Detect's out overload tells when that decided the result.
/// Index and ReadShaders go to the reader of <see cref="EngineInfo.Family"/>, so an EngineInfo cached from an earlier scan
/// still finds its reader.</summary>
public sealed class EngineReaders(params (string Family, IEngineReader Reader)[] readers) : IEngineReader
{
    public T? Get<T>() where T : class, IEngineReader => readers.Select(r => r.Reader).OfType<T>().FirstOrDefault();

    public EngineInfo? Detect(Game game) => Detect(game, out _);

    /// <summary><see cref="Detect(Game)"/>, with <paramref name="skipped"/> a reader before the one whose result it returned,
    /// and what it threw: the game was taken for something else because of it. Returned, not kept: one game detected on two
    /// threads at once (a background redetect beside a scan) gets each its own.</summary>
    public EngineInfo? Detect(Game game, out (string Family, Exception Error)? skipped)
    {
        EngineInfo? first = null;
        (string Family, Exception Error)? error = null, before = null;
        skipped = null;
        foreach (var (family, reader) in readers)
        {
            EngineInfo? e;
            try { e = reader.Detect(game); }
            catch (Exception ex) { error ??= (family, ex); continue; } // one reader choking on the game's files doesn't stop the next
            if (e == null) continue;
            if (e.Unsupported == null && !e.Encrypted) { skipped = error; return e; }
            if (first == null) (first, before) = (e, error);
        }
        if (first == null && error is { } thrown) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(thrown.Error);
        if (first != null) skipped = before;
        return first;
    }

    public string IndexStamp(Game game) => string.Concat(readers.Select(r => r.Reader.IndexStamp(game)));

    public string DetectStamp(Game game, EngineInfo? engine) => engine == null ? string.Concat(readers.Select(r => r.Reader.DetectStamp(game, null)))
        : readers.FirstOrDefault(r => r.Family == engine.Family).Reader?.DetectStamp(game, engine) ?? "";

    IEngineReader Of(EngineInfo e) => readers.FirstOrDefault(r => r.Family == e.Family).Reader ?? throw new NotSupportedException($"no reader for {e.Family}");

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => Of(engine).Index(game, engine, log, ct);

    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) =>
        Of(engine).ReadShaders(game, engine, sha1s, sink, ct);
}
