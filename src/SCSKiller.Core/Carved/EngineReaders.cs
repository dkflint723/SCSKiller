namespace SCSKiller.Core.Carved;

/// <summary>Several engine readers as one, tried in order. Detect: the first result that can be indexed, else the first
/// reader's that recognized the game (its reason is the most specific, e.g. Unreal's "encrypted" over the carver's "packed").
/// A reader that throws is skipped; <see cref="Skipped"/> tells when that decided the result.
/// Index and ReadShaders go to the reader of <see cref="EngineInfo.Family"/>, so an EngineInfo cached from an earlier scan
/// still finds its reader.</summary>
public sealed class EngineReaders(params (string Family, IEngineReader Reader)[] readers) : IEngineReader
{
    public T? Get<T>() where T : class, IEngineReader => readers.Select(r => r.Reader).OfType<T>().FirstOrDefault();

    public EngineInfo? Detect(Game game)
    {
        EngineInfo? first = null;
        (string Family, Exception Error)? error = null, before = null;
        skipped.TryRemove(game.Id, out _);
        foreach (var (family, reader) in readers)
        {
            EngineInfo? e;
            try { e = reader.Detect(game); }
            catch (Exception ex) { error ??= (family, ex); continue; } // one reader choking on the game's files doesn't stop the next
            if (e == null) continue;
            if (e.Unsupported == null && !e.Encrypted) return Skip(game, error, e);
            if (first == null) (first, before) = (e, error);
        }
        if (first == null && error is { } thrown) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(thrown.Error);
        return Skip(game, before, first);
    }

    EngineInfo? Skip(Game game, (string, Exception)? error, EngineInfo? e)
    {
        if (error is { } s && e != null) skipped[game.Id] = s;
        return e;
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Family, Exception Error)> skipped = new();

    /// <summary>A reader before the one whose result the game's last <see cref="Detect"/> returned, and what it threw: the
    /// game was taken for something else because of it.</summary>
    public (string Family, Exception Error)? Skipped(Game game) => skipped.TryGetValue(game.Id, out var s) ? s : null;

    public string IndexStamp(Game game) => string.Concat(readers.Select(r => r.Reader.IndexStamp(game)));

    public string DetectStamp(Game game, EngineInfo? engine) => engine == null ? string.Concat(readers.Select(r => r.Reader.DetectStamp(game, null)))
        : readers.FirstOrDefault(r => r.Family == engine.Family).Reader?.DetectStamp(game, engine) ?? "";

    IEngineReader Of(EngineInfo e) => readers.FirstOrDefault(r => r.Family == e.Family).Reader ?? throw new NotSupportedException($"no reader for {e.Family}");

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => Of(engine).Index(game, engine, log, ct);

    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) =>
        Of(engine).ReadShaders(game, engine, sha1s, sink, ct);
}
