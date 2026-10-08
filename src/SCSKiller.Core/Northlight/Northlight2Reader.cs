using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Northlight;

/// <summary>Northlight as CONTROL Resonant ships it: the DX12 effect files of data\shaders\build\pc_dx12 (.binrfx, magic
/// "RFX ", layout 'O'), raw and uncompressed (<see cref="Parse"/>): DXIL vertex, pixel, compute and mesh shaders and ray
/// tracing libraries. Each technique entry names a shader per group (VS, PS, CS, MS, library): its VS+PS, its MS+PS and its
/// CS are each one IsPipeline map, the libraries one pool. Shaders carry no root signature: the engine builds one per kind of
/// pipeline (<see cref="Planning.RootSig.Rule.Northlight2"/>). The game's material shaders aren't in these files (its packs
/// compress them): a recording adds those. Alan Wake 2's effect files (layout ':') are laid out otherwise and not read.
/// A fork-only reader with its own family, so what it plans is never shared as Northlight's or the carver's.
/// EngineInfo: Family "Northlight2", Version "DX12", D3D12.</summary>
public sealed class Northlight2Reader : IEngineReader
{
    public const string Family = "Northlight2", Version = "DX12";
    public const string Libraries = "libraries";
    const string Dir = @"data\shaders\build\pc_dx12";
    const uint Layout = 'O';

    /// <summary>The stage each shader group of an effect file holds, in file order (<see cref="Dxbc.Kind"/>): VS, PS, CS, MS,
    /// libraries; a technique entry has one slot per group.</summary>
    static readonly int[] Groups = [1, 0, 5, 13, 6];
    const int Vs = 0, Ps = 1, Cs = 2, Ms = 3, Lib = 4;

    /// <summary>Effect files Detect reads, smallest first, to find one technique entry (CONTROL Resonant's 5 smallest hold no shader).</summary>
    const int DetectFiles = 16;

    /// <summary>Bounds on what the index takes in (CONTROL Resonant: 23 MB for its largest effect file, 1,259 shaders in all,
    /// 930 distinct pipelines): a bigger file is skipped, more pipelines are left out with a warning.</summary>
    const long MaxFile = 512L << 20;
    const int MaxShadersPerFile = 1 << 18;
    internal int MaxPipelines { get; init; } = 1 << 20;

    public sealed record Loc(string Path, long Offset, int Size);

    /// <summary>Where each container is (by SHA-1), per game id, from the last Index; in memory only, like Northlight's.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    public EngineInfo? Detect(Game game)
    {
        var dir = Path.Combine(game.InstallDir, Dir);
        if (!Directory.Exists(dir)) return null;
        foreach (var f in Effects(dir).OrderBy(f => f.Length).Take(DetectFiles))
            if (Parse(Read(f.FullName), default) is { Entries.Count: > 0 })
                return new EngineInfo(Family, Version, null, "D3D12", false, null);
        return null;
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var maps = new List<ShaderMap>();
        var seen = new HashSet<string>();
        var libs = new List<string>();
        int pipelines = 0, visited = 0, pooled = 0, unlinked = 0, overCap = 0, bad = 0, files = 0;
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var f in Effects(Path.Combine(game.InstallDir, Dir)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(game.InstallDir, f.FullName);
            var b = Read(f.FullName);
            if (Parse(b, ct) is not { } effect) continue;
            files++;
            content.AppendData(Encoding.UTF8.GetBytes($"{rel}|{f.Length}|{f.LastWriteTimeUtc.Ticks}\n"));
            var shas = new string?[effect.Shaders.Count];
            for (var i = 0; i < shas.Length; i++)
            {
                var s = effect.Shaders[i];
                var bytes = b.AsSpan(s.Offset, s.Size);
                var sha = Convert.ToHexStringLower(SHA1.HashData(bytes));
                if (locs.TryAdd(sha, new Loc(f.FullName, s.Offset, s.Size)))
                    try { if (ShaderContainer.Parse(bytes, sha, new(0, 0, 0, 0)) is { } info) shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings), RootSignature = null }; }
                    catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { bad++; } // valid container, odd program: not usable
                if (shaders.ContainsKey(sha)) shas[i] = sha;
            }
            var used = new HashSet<int>();
            foreach (var e in effect.Entries)
            {
                if (e[Lib] >= 0 && shas[e[Lib]] is { } lib) { libs.Add(lib); used.Add(e[Lib]); }
                foreach (var p in new[] { e[Cs] >= 0 ? new[] { e[Cs] } : null, e[Vs] >= 0 ? [e[Vs], e[Ps]] : null, e[Ms] >= 0 ? [e[Ms], e[Ps]] : null })
                {
                    if (p == null) continue;
                    var st = p.Where(i => i >= 0).ToList();
                    if (st.Any(i => shas[i] == null)) continue;   // unparseable: counted above
                    used.UnionWith(st);
                    if (visited++ >= MaxPipelines) { overCap++; continue; }
                    var infos = st.Select(i => shaders[shas[i]!]).ToList();
                    // a VS that doesn't feed its PS is left out (the runtime rejects it); an MS+PS pair is the game's as named
                    // (Planner.MeshFeeds rejects 6 of the 100 its recording creates)
                    if (infos is [{ Stage: Stage.Vertex } vs, var ps] && !Planning.Planner.Links(vs, ps)) { unlinked++; continue; }
                    pipelines++;
                    var set = st.Select(i => shas[i]!).ToList();
                    var h = CarvedReader.Sha1Hex(string.Join(',', set.Order(StringComparer.Ordinal)));
                    if (seen.Add(h)) maps.Add(new ShaderMap(h, rel, CarvedReader.Platform, set, IsPipeline: true));
                }
            }
            // a shader no entry names (none in CONTROL Resonant): pooled per file, paired like the carver's
            var pool = Enumerable.Range(0, shas.Length).Where(i => !used.Contains(i) && shas[i] != null && effect.Shaders[i].Group != Lib).Select(i => shas[i]!).Distinct().ToList();
            pooled += pool.Count;
            if (pool.Count > 0) maps.Add(new ShaderMap(CarvedReader.Sha1Hex(rel), rel, CarvedReader.Platform, pool));
        }
        libs = libs.Distinct().ToList();
        if (libs.Count > 0) maps.Add(new ShaderMap(CarvedReader.Sha1Hex($"{Dir}|{Libraries}"), Libraries, CarvedReader.Platform, libs));
        if (pipelines == 0) throw new InvalidDataException($"{Dir}: no technique entry in its effect files: scan the game again");
        located[game.Id] = locs;
        log?.Report($"{files} effect files, {shaders.Count} shaders ({string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))}), "
            + $"{pipelines} technique pipelines -> {maps.Count(m => m.IsPipeline)} distinct, {libs.Count} ray tracing libraries"
            + (unlinked > 0 ? $"; {unlinked} left out: their VS or MS doesn't feed their PS" : "")
            + (overCap > 0 ? $"; warning: {overCap} pipelines over the cap of {MaxPipelines} left out" : "")
            + (pooled > 0 ? $"; {pooled} shaders no technique names, pooled per file" : "") + (bad > 0 ? $"; {bad} unparseable" : "")
            + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), [CarvedReader.Platform], shaders, maps);
    }

    /// <summary>Re-slices each container where the index found it; one whose bytes changed since (game patched) is skipped.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        foreach (var file in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.Path))
        {
            Microsoft.Win32.SafeHandles.SafeFileHandle h;
            try { h = File.OpenHandle(file.Key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; } // gone since the index
            using var _ = h;
            foreach (var (sha, at) in file.OrderBy(x => x.At.Offset))
            {
                ct.ThrowIfCancellationRequested();
                var b = new byte[at.Size];
                if (RandomAccess.Read(h, b, at.Offset) == b.Length && Convert.ToHexStringLower(SHA1.HashData(b)) == sha) sink(sha, b);
            }
        }
    }

    public readonly record struct Shader(int Offset, int Size, int Group);

    /// <summary>An effect file's shaders in file order and its distinct technique entries: per group the index of the entry's
    /// shader in <paramref name="Shaders"/>, -1 for none.</summary>
    public sealed record Effect(List<Shader> Shaders, List<int[]> Entries);

    /// <summary>An effect file of layout 'O' (measured on CONTROL Resonant's 141): magic, u32 layout, u32 length + name; then
    /// per group (<see cref="Groups"/>) a u32 count and its shaders, each u32 length + entry point (empty for a library), u64
    /// size, the container, its reflection, an 8-byte id and a u32; then a u32 technique count and per technique u32 length +
    /// name, u32 entry count and entries of 5 ids (one per group, 0 for none) and a u32 permutation key. The reflection's length
    /// isn't parsed: a shader's id is where the next shader's header (or, after the last one, the technique table) puts it, and
    /// the entries are found by their ids. Null when the bytes don't follow this layout.</summary>
    public static Effect? Parse(byte[] b, CancellationToken ct)
    {
        if (b.Length < 16 || !b.AsSpan(0, 4).SequenceEqual("RFX "u8) || U(b, 4) != Layout || U(b, 8) > b.Length - 12) return null;
        var start = 12 + (int)U(b, 8);
        var shaders = new List<Shader>();
        var ids = new Dictionary<ulong, int>();
        var counts = new uint[Groups.Length];
        var g = -1;
        var end = start;   // where the last shader's container ends
        foreach (var (at, c) in Dxbc.Containers(b))
        {
            if (shaders.Count >= MaxShadersPerFile) return null;
            if (shaders.Count % 4096 == 4095) ct.ThrowIfCancellationRequested();
            var group = Array.IndexOf(Groups, Dxbc.Kind(c));
            if (group < 0 || group < g || at < end + 12 || BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(at - 8)) != c.Length || Header(b, at, end) is not { } q) return null;
            // a group's count before its first shader, a zero count per group skipped before it, and before those the previous
            // shader's id and u32 (the file header's name for the first shader)
            var skipped = group - g - 1;
            if (group > g && ((counts[group] = U(b, q - 4)) == 0 || q - 4 - 4 * skipped < start)) return null;
            for (var x = 0; group > g && x < skipped; x++) if (U(b, q - 8 - 4 * x) != 0) return null;
            var idEnd = group == g ? q - 4 : q - 8 - 4 * skipped;
            if (g < 0 ? q - 4 - 4 * skipped != start
                : idEnd - 8 < end || !ids.TryAdd(BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(idEnd - 8)), shaders.Count - 1)) return null;
            shaders.Add(new Shader(at, c.Length, group));
            g = group;
            end = at + c.Length;
        }
        if (shaders.Count == 0) return U(b, start) == 0 ? new Effect([], []) : null;
        for (var x = 0; x < Groups.Length; x++) if (shaders.Count(s => s.Group == x) != counts[x]) return null;
        // the last shader's id: after it its u32, a zero count per group left, the technique count, the first technique's name
        // and entry count, then an entry naming only shaders of this file (the last one by this id)
        var left = Groups.Length - 1 - g;
        for (var q = end; q + 12 + 4 * left + 8 <= b.Length; q++)
        {
            var id = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(q));
            if (id == 0 || ids.ContainsKey(id)) continue;
            var t = q + 12 + 4 * left;
            var n = (int)Math.Min(U(b, t + 4), 256);
            if (b.AsSpan(q + 12, 4 * left).ContainsAnyExcept((byte)0) || U(b, t) is 0 or > 1 << 20 || n is 0 or > 255
                || t + 12 + n + 8 * Groups.Length > b.Length || !Name(b.AsSpan(t + 8, n)) || U(b, t + 8 + n) is 0 or > 1 << 20) continue;
            ids[id] = shaders.Count - 1;
            if (Entry(b, t + 12 + n, ids, shaders) != null) return new Effect(shaders, Entries(b, t, ids, shaders, ct));
            ids.Remove(id);
        }
        return null;
    }

    /// <summary>The distinct entries at or after <paramref name="from"/>: 5 ids in a row, each 0 or one of this file's shaders of
    /// that slot's group.</summary>
    static List<int[]> Entries(byte[] b, int from, Dictionary<ulong, int> ids, List<Shader> shaders, CancellationToken ct)
    {
        var found = new Dictionary<string, int[]>();
        for (int q = from, k = 0; q + 8 * Groups.Length <= b.Length; k++)
        {
            if ((k & 0xFFFFF) == 0) ct.ThrowIfCancellationRequested();
            if (Entry(b, q, ids, shaders) is not { } e) { q++; continue; }
            found.TryAdd(string.Join(',', e), e);
            q += 8 * Groups.Length;
        }
        return [.. found.Values];
    }

    static int[]? Entry(byte[] b, int at, Dictionary<ulong, int> ids, List<Shader> shaders)
    {
        var e = new int[Groups.Length];
        var any = false;
        for (var s = 0; s < e.Length; s++)
        {
            var id = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at + 8 * s));
            if (id == 0) e[s] = -1;
            else if (ids.TryGetValue(id, out var i) && shaders[i].Group == s) (e[s], any) = (i, true);
            else return null;
        }
        return any ? e : null;
    }

    /// <summary>Where the shader at <paramref name="at"/>'s header starts: u32 length and its entry point (empty for a library),
    /// then the u64 size before the container; null when it isn't there (after <paramref name="min"/>).</summary>
    static int? Header(byte[] b, int at, int min)
    {
        for (var n = 0; n <= 255; n++)
        {
            var q = at - 12 - n;
            if (q < min) return null;
            if (U(b, q) == n && Name(b.AsSpan(q + 4, n))) return q;
        }
        return null;
    }

    static bool Name(ReadOnlySpan<byte> s)
    {
        foreach (var ch in s) if (!(char.IsAsciiLetterOrDigit((char)ch) || ch == '_')) return false;
        return true;
    }

    static uint U(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));

    static IEnumerable<FileInfo> Effects(string dir) =>
        Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*.binrfx", new EnumerationOptions { IgnoreInaccessible = true }) : [];

    /// <summary>The file's bytes when it starts with the effect magic and is at most <see cref="MaxFile"/>; else empty.</summary>
    static byte[] Read(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> magic = stackalloc byte[4];
            if (f.Length > MaxFile || f.ReadAtLeast(magic, 4, false) < 4 || !magic.SequenceEqual("RFX "u8)) return [];
            var b = new byte[f.Length];
            magic.CopyTo(b);
            f.ReadExactly(b.AsSpan(4));
            return b;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
