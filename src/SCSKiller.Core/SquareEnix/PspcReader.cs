using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.SquareEnix;

/// <summary>FINAL FANTASY XVI's pipeline list, <c>&lt;exe name&gt;.pspc</c> beside the exe (the exe builds the name from
/// "%s%s.pspc"): every pipeline the game creates, with its raw shader containers (DXIL SM 6.6, a few DXBC SM 5.0) and its
/// root signature (a container of one RTS0 part, version 1.0). Layout (measured on 0x0300000A): a 0xC0-byte header whose
/// offsets count from its end; "PSPC", u32 version, 8 bytes, u64 size of the rest; at 0x18 six u64 section offsets and at
/// 0x48 six u32 sizes (VS, PS, GS, HS, DS, CS); at 0x60 ten (u32 count, u32 offset) tables (<see cref="Kinds"/>; 3 is
/// empty, 5-9 hold state: 6 the input layouts, the others blend, depth-stencil and rasterizer state, not decoded); at 0xB0
/// the root-signature section's u64 offset and u64 size. An entry is u64 hash, u32 root-signature offset (to a u32 size
/// and the container), the graphics ones 3 u32 of packed state, then a u32 offset per stage to its container. Every
/// distinct entry (stages + root signature) is one IsPipeline map with its root signature (<see cref="ShaderMap.RootSignature"/>):
/// a shader may be drawn under several. Nothing is decompressed; every count, offset and size is bounded by the file.
/// EngineInfo: Family "Square Enix PSPC", Version the file's ("0x0300000A"), ShipsRootSignatures.</summary>
public sealed class PspcReader : IEngineReader
{
    public const string Family = "Square Enix PSPC";
    public const uint SupportedVersion = 0x0300000A;
    const int Header = 0xC0;
    const int Rs = 6;   // the root-signature section, after the six stage sections

    /// <summary>Entries per table at most (FINAL FANTASY XVI: 68,723 in the largest), so a table is read whole within 40 MB.</summary>
    internal const int MaxEntries = 1 << 20;

    /// <summary>The pipeline tables: entry size, then per stage the u32 field holding its offset and its section.</summary>
    internal static readonly (int Table, int Size, (int Field, int Section, Stage Stage)[] Stages)[] Kinds =
    [
        (0, 32, [(6, 0, Stage.Vertex), (7, 1, Stage.Pixel)]),
        (1, 40, [(6, 0, Stage.Vertex), (7, 2, Stage.Geometry), (8, 1, Stage.Pixel)]),
        (2, 40, [(6, 0, Stage.Vertex), (7, 3, Stage.Hull), (8, 4, Stage.Domain), (9, 1, Stage.Pixel)]),
        (4, 16, [(3, 5, Stage.Compute)]),
    ];

    public sealed record Loc(string Path, long Offset, int Size);

    /// <summary>Where each container is (by SHA-1), per game id, from the last Index. In memory only, like the carver's;
    /// ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    /// <summary>A parsed header: each section's absolute offset and size (VS, PS, GS, HS, DS, CS, root signatures) and each
    /// table's entry count and absolute offset.</summary>
    internal sealed record Layout(uint Version, (long Off, long Size)[] Sections, (int Count, long Off)[] Tables);

    public EngineInfo? Detect(Game game)
    {
        string? why = null;
        foreach (var path in Candidates(game))
            try
            {
                using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (Read(h, out var problem) is not { } layout)
                {
                    if (problem != null) why ??= $"{Path.GetFileName(path)}: {problem}";
                    continue;
                }
                if (FirstPipeline(h, layout))
                    return new EngineInfo(Family, $"0x{layout.Version:X8}", null, "D3D12", false, null, ShipsRootSignatures: true);
                why ??= $"{Path.GetFileName(path)}: its first pipeline doesn't read as this layout";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return why == null ? null : new EngineInfo(Family, "-", null, "D3D12", false, why);
    }

    /// <summary><c>&lt;exe name&gt;.pspc</c> beside the exe, then any other .pspc there.</summary>
    static IEnumerable<string> Candidates(Game game)
    {
        var dir = Path.GetDirectoryName(game.ExePath);
        if (dir == null || !Directory.Exists(dir)) yield break;
        var own = Path.ChangeExtension(game.ExePath, ".pspc");
        if (File.Exists(own)) yield return own;
        foreach (var f in Directory.EnumerateFiles(dir, "*.pspc", new EnumerationOptions { IgnoreInaccessible = true }).Order(StringComparer.OrdinalIgnoreCase))
            if (!f.Equals(own, StringComparison.OrdinalIgnoreCase)) yield return f;
    }

    /// <summary>The header, or null with why (null too when the file isn't a pipeline list at all).</summary>
    internal static Layout? Read(SafeFileHandle h, out string? why)
    {
        why = null;
        var length = RandomAccess.GetLength(h);
        var b = new byte[Header];
        if (length < Header || RandomAccess.Read(h, b, 0) != Header || !b.AsSpan(0, 4).SequenceEqual("PSPC"u8)) return null;
        var version = U32(b, 4);
        if (version != SupportedVersion) { why = $"pipeline list version 0x{version:X8} isn't supported yet"; return null; }
        why = "damaged or truncated: verify the game's files";
        if (U64(b, 0x10) != (ulong)(length - Header)) return null;
        var sections = new (long, long)[7];
        for (var i = 0; i <= Rs; i++)
        {
            ulong off = U64(b, i < Rs ? 0x18 + 8 * i : 0xB0), size = i < Rs ? U32(b, 0x48 + 4 * i) : U64(b, 0xB8);
            if (!Within(off, size, length)) return null;
            sections[i] = (Header + (long)off, (long)size);
        }
        var tables = new (int, long)[10];
        for (var i = 0; i < 10; i++) tables[i] = ((int)Math.Min(U32(b, 0x60 + 8 * i), int.MaxValue), Header + (long)U32(b, 0x64 + 8 * i));
        foreach (var (t, size, _) in Kinds)
            if (tables[t].Item1 > MaxEntries || !Within((ulong)(tables[t].Item2 - Header), (ulong)tables[t].Item1 * (ulong)size, length)) return null;
        why = null;
        return new Layout(version, sections, tables);
    }

    /// <summary>[Header + off, + size) lies in the file.</summary>
    static bool Within(ulong off, ulong size, long length) => off <= (ulong)length && size <= (ulong)length - off && Header + off + size <= (ulong)length;

    /// <summary>The first entry of the first pipeline table that has one names a root signature and containers of its stages.</summary>
    static bool FirstPipeline(SafeFileHandle h, Layout l)
    {
        foreach (var (t, size, stages) in Kinds)
        {
            if (l.Tables[t].Count == 0) continue;
            var e = new byte[size];
            if (RandomAccess.Read(h, e, l.Tables[t].Off) != size || Container(h, l, Rs, U32(e, 8)) is not { } rs || !Dxbc.IsRootSignatureOnly(rs)) return false;
            return stages.All(s => Container(h, l, s.Section, U32(e, 4 * s.Field)) is { } c && Kind(c) == s.Stage);
        }
        return false;
    }

    /// <summary>The container at <paramref name="rel"/> in a section, whole and valid, within the section; a root
    /// signature's u32 size first. Null otherwise.</summary>
    static byte[]? Container(SafeFileHandle h, Layout l, int section, uint rel)
    {
        var (off, size) = l.Sections[section];
        var skip = section == Rs ? 4 : 0;
        if (rel + skip + 32L > size) return null;
        Span<byte> head = stackalloc byte[36];
        var at = off + rel;
        if (RandomAccess.Read(h, head[..(skip + 32)], at) != skip + 32) return null;
        var n = Dxbc.HeaderSize(head[skip..]);
        if (n == 0 || rel + skip + (long)n > size || skip > 0 && BinaryPrimitives.ReadUInt32LittleEndian(head) != n) return null;
        var c = new byte[n];
        return RandomAccess.Read(h, c, at + skip) == n && Dxbc.Valid(c) ? c : null;
    }

    static Stage? Kind(ReadOnlySpan<byte> c) => Dxbc.Kind(c) switch
    {
        0 => Stage.Pixel, 1 => Stage.Vertex, 2 => Stage.Geometry, 3 => Stage.Hull, 4 => Stage.Domain, 5 => Stage.Compute, _ => null,
    };

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string? why = null;
        foreach (var path in Candidates(game))
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (Read(h, out var problem) is { } layout && FirstPipeline(h, layout)) return Index(game, path, h, layout, log, ct, sw);
            why ??= problem;
        }
        throw new InvalidDataException($"no pipeline list (.pspc) beside the exe{(why != null ? $" ({why})" : "")}: scan the game again");
    }

    ShaderIndex Index(Game game, string path, SafeFileHandle h, Layout l, IProgress<string>? log, CancellationToken ct, Stopwatch sw)
    {
        // the entries, each table read whole (bounded by MaxEntries)
        var entries = new List<(int Kind, uint Rs, uint[] Stages)>();
        for (var k = 0; k < Kinds.Length; k++)
        {
            var (t, size, stages) = Kinds[k];
            var b = new byte[l.Tables[t].Count * size];
            if (RandomAccess.Read(h, b, l.Tables[t].Off) != b.Length) throw new InvalidDataException($"{path}: truncated table {t}");
            for (var e = 0; e < l.Tables[t].Count; e++)
                entries.Add((k, U32(b, e * size + 8), [.. stages.Select(s => U32(b, e * size + 4 * s.Field))]));
        }
        ct.ThrowIfCancellationRequested();

        // each distinct container once, in file order
        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var at = new Dictionary<(int Section, uint Rel), string?>();   // null: not a usable container of its slot
        var platformOf = new Dictionary<string, string>();
        var rootSigs = new HashSet<string>();
        int bad = 0;
        var wanted = entries.SelectMany(e => e.Stages.Select((rel, i) => (Kinds[e.Kind].Stages[i].Section, rel)).Append((Rs, e.Rs))).Distinct()
            .OrderBy(x => l.Sections[x.Item1].Off + x.Item2).ToList();
        foreach (var (section, rel) in wanted)
        {
            if (at.Count % 4096 == 0) ct.ThrowIfCancellationRequested();
            at[(section, rel)] = null;
            if (Container(h, l, section, rel) is not { } c) continue;
            var sha = Convert.ToHexStringLower(SHA1.HashData(c));
            if (section == Rs)
            {
                if (!Dxbc.IsRootSignatureOnly(c) || !Dxbc.RootSignatureValid(c)) continue;
                rootSigs.Add(sha);
            }
            else if (!shaders.ContainsKey(sha))
                try
                {
                    if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is not { } info) continue;
                    shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings), RootSignature = null };
                    if (CarvedReader.LanePlatform(Dxbc.WaveLanes(c)) is { } lanes) platformOf[sha] = lanes;
                }
                catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { bad++; continue; } // valid container, odd program: not usable
            at[(section, rel)] = sha;
            locs.TryAdd(sha, new Loc(path, l.Sections[section].Off + rel + (section == Rs ? 4 : 0), c.Length));
        }

        // one exact map per distinct entry: its stages and its root signature
        var rel0 = Path.GetRelativePath(game.InstallDir, path);
        var maps = new List<ShaderMap>();
        var seen = new HashSet<string>();
        var perKind = new int[Kinds.Length];
        int unusable = 0, wrongStage = 0;
        foreach (var (k, rsRel, rels) in entries)
        {
            var rs = at[(Rs, rsRel)];
            var shas = rels.Select((rel, i) => at[(Kinds[k].Stages[i].Section, rel)]).ToList();
            if (rs == null || shas.Any(s => s == null)) { unusable++; continue; }
            if (shas.Where((s, i) => shaders[s!].Stage != Kinds[k].Stages[i].Stage).Any()) { wrongStage++; continue; }
            perKind[k]++;
            var hash = CarvedReader.Sha1Hex($"{rs}|{string.Join(',', shas)}");
            if (seen.Add(hash))
                maps.Add(new ShaderMap(hash, rel0, shas.Select(s => platformOf.GetValueOrDefault(s!, CarvedReader.Platform)).FirstOrDefault(p => p != CarvedReader.Platform, CarvedReader.Platform),
                    [.. shas.OfType<string>()], IsPipeline: true, RootSignature: rs));
        }
        if (maps.Count == 0) throw new InvalidDataException($"{rel0}: no pipeline in it reads as this layout: scan the game again");
        located[game.Id] = locs;

        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var file = new FileInfo(path);
        content.AppendData(Encoding.UTF8.GetBytes($"{rel0}|{file.Length}|{file.LastWriteTimeUtc.Ticks}\n"));
        var head = new byte[Header];
        RandomAccess.Read(h, head, 0);
        content.AppendData(head);
        var unknown = l.Tables[3].Count;
        log?.Report($"{rel0}: {entries.Count} pipelines ({string.Join(", ", perKind.Select((n, k) => $"{n} {string.Join('+', Kinds[k].Stages.Select(s => Abbrev(s.Stage)))}"))}) -> {maps.Count} distinct, "
            + $"{shaders.Count} shaders ({string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))}), {rootSigs.Count} root signatures"
            + (unusable > 0 ? $"; {unusable} left out: a container that doesn't read" : "") + (wrongStage > 0 ? $"; {wrongStage} left out: a shader of another stage than its slot" : "")
            + (bad > 0 ? $"; {bad} unparseable" : "") + (unknown > 0 ? $"; warning: {unknown} entries of table 3 (an unknown kind) left out" : "")
            + string.Concat(platformOf.Values.GroupBy(p => p).Select(g => $"; {g.Count()} need {g.Key}, not planned for 32-lane GPUs")) + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), [CarvedReader.Platform, .. platformOf.Values.Distinct().Order(StringComparer.Ordinal)], shaders, maps);
    }

    static string Abbrev(Stage s) => s switch { Stage.Vertex => "VS", Stage.Pixel => "PS", Stage.Geometry => "GS", Stage.Hull => "HS", Stage.Domain => "DS", _ => "CS" };

    /// <summary>Re-slices each container (shaders and root signatures) where the index found it; one whose bytes changed
    /// since (game patched) is skipped.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        foreach (var file in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.Path))
        {
            SafeFileHandle h;
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

    static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
    static ulong U64(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b[at..]);
}
