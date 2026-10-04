using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using SCSKiller.Core.SquareEnix;
using SCSKiller.Tests.Carved;
using SCSKiller.Tests.Planning;

namespace SCSKiller.Tests.SquareEnix;

/// <summary>The PSPC reader on a synthetic pipeline list laid out as FINAL FANTASY XVI's ffxvi.pspc, with shaders and root
/// signatures compiled here (<see cref="Hlsl"/>; SM 5.0, no RTS0 in the shaders, root signatures 1.0 as the game's).</summary>
public class PspcReaderTests
{
    const string VsSrc = "float4 main(float3 p : POSITION, float2 uv : TEXCOORD0, out float2 ouv : TEXCOORD0) : SV_Position { ouv = uv; return float4(p * {0}.0, 1); }";
    const string PsSrc = "Texture2D t : register(t0); SamplerState s : register(s0); float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return t.Sample(s, uv) * {0}.0; }";
    const string GsSrc = """
        struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        [maxvertexcount(3)] void main(triangle V i[3], inout TriangleStream<V> o) { for (int k = 0; k < 3; k++) o.Append(i[k]); }
        """;
    const string Cp = "struct CP { float3 p : POS; float2 uv : TEXCOORD0; }; struct PC { float e[3] : SV_TessFactor; float i : SV_InsideTessFactor; };";
    const string VsTessSrc = Cp + "CP main(float3 p : POSITION, float2 uv : TEXCOORD0) { CP o; o.p = p; o.uv = uv; return o; }";
    const string HsSrc = Cp + """
        PC pc(InputPatch<CP, 3> ip) { PC o; o.e[0] = 1; o.e[1] = 1; o.e[2] = 1; o.i = 1; return o; }
        [domain("tri")] [partitioning("integer")] [outputtopology("triangle_cw")] [outputcontrolpoints(3)] [patchconstantfunc("pc")]
        CP main(InputPatch<CP, 3> ip, uint i : SV_OutputControlPointID) { return ip[i]; }
        """;
    const string DsSrc = Cp + """
        struct O { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        [domain("tri")] O main(PC pc, float3 b : SV_DomainLocation, const OutputPatch<CP, 3> p) { O o; o.pos = float4(p[0].p * b.x + p[1].p * b.y + p[2].p * b.z, 1); o.uv = p[0].uv; return o; }
        """;
    const string CsSrc = "RWByteAddressBuffer u : register(u0); [numthreads(64, 1, 1)] void main(uint i : SV_DispatchThreadID) { u.Store(i * 4, i * {0}); }";
    const string RsA = "RootFlags(ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT), DescriptorTable(SRV(t0)), StaticSampler(s0)";
    const string RsB = "RootFlags(ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT), CBV(b0), DescriptorTable(SRV(t0)), StaticSampler(s0)";
    const string RsC = "DescriptorTable(UAV(u0))";

    /// <summary>A pipeline entry: its root signature and its stages' containers in its table's order (<see cref="PspcReader.Kinds"/>).</summary>
    internal sealed record Entry(byte[] Rs, params byte[][] Stages);

    sealed class Fixture
    {
        public readonly string Dir = Ff7.TempDir("pspc-synthetic");
        public readonly byte[] Vs1 = Hlsl.Compile(VsSrc.Replace("{0}", "1"), "main", "vs_5_0"), Vs2 = Hlsl.Compile(VsSrc.Replace("{0}", "2"), "main", "vs_5_0");
        public readonly byte[] Ps1 = Hlsl.Compile(PsSrc.Replace("{0}", "1"), "main", "ps_5_0"), Ps2 = Hlsl.Compile(PsSrc.Replace("{0}", "2"), "main", "ps_5_0");
        public readonly byte[] Gs = Hlsl.Compile(GsSrc, "main", "gs_5_0"), VsTess = Hlsl.Compile(VsTessSrc, "main", "vs_5_0");
        public readonly byte[] Hs = Hlsl.Compile(HsSrc, "main", "hs_5_0"), Ds = Hlsl.Compile(DsSrc, "main", "ds_5_0");
        public readonly byte[] Cs1 = Hlsl.Compile(CsSrc.Replace("{0}", "1"), "main", "cs_5_0"), Cs2 = Hlsl.Compile(CsSrc.Replace("{0}", "2"), "main", "cs_5_0");
        public readonly byte[] A = RootSignature(RsA), B = RootSignature(RsB), C = RootSignature(RsC);
        public Game Game => new("test:pspc", "pspc", Store.Other, Dir, Path.Combine(Dir, "ffxvi.exe"));
        public string File => Path.Combine(Dir, "ffxvi.pspc");

        /// <summary>VS+PS: VS1+PS1 under A and under B (one shader set, two root signatures), VS2+PS2, VS1+PS1 under A again;
        /// VS+GS+PS; VS+HS+DS+PS; two compute shaders.</summary>
        public Entry[][] Entries =>
        [
            [new(A, Vs1, Ps1), new(B, Vs1, Ps1), new(A, Vs2, Ps2), new(A, Vs1, Ps1)],
            [new(A, Vs1, Gs, Ps1)],
            [new(A, VsTess, Hs, Ds, Ps1)],
            [new(C, Cs1), new(C, Cs2)],
        ];

        public Fixture()
        {
            Directory.CreateDirectory(Dir);
            System.IO.File.WriteAllBytes(File, Pspc(Entries));
        }
    }

    static readonly Lazy<Fixture> Data = new(() => new Fixture());
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));
    static byte[] RootSignature(string rs) => Hlsl.Compile($"#define RS \"{rs}\"\n", "RS", "rootsig_1_0");

    /// <summary>A pipeline list: the 0xC0-byte header (offsets from its end), the ten tables (the four pipeline kinds,
    /// table 6 a stand-in for the input layouts, the other state tables empty), then the sections VS, PS, GS, HS, DS, CS and
    /// root signatures, each container once (a root signature after its u32 size), 16-byte aligned.</summary>
    internal static byte[] Pspc(Entry[][] kinds, uint version = PspcReader.SupportedVersion)
    {
        var secs = Enumerable.Range(0, 7).Select(_ => new MemoryStream()).ToArray();
        var at = Enumerable.Range(0, 7).Select(_ => new Dictionary<string, uint>()).ToArray();
        uint Put(int s, byte[] c)
        {
            if (at[s].TryGetValue(Sha(c), out var o)) return o;
            at[s][Sha(c)] = o = (uint)secs[s].Length;
            if (s == 6) secs[s].Write(BitConverter.GetBytes(c.Length));
            secs[s].Write(c);
            while (secs[s].Length % 16 != 0) secs[s].WriteByte(0);
            return o;
        }
        var tables = new byte[10][];
        var counts = new int[10];
        for (var k = 0; k < PspcReader.Kinds.Length; k++)
        {
            var (t, size, stages) = PspcReader.Kinds[k];
            var b = tables[t] = new byte[kinds[k].Length * size];
            counts[t] = kinds[k].Length;
            for (var i = 0; i < kinds[k].Length; i++)
            {
                BitConverter.TryWriteBytes(b.AsSpan(i * size), (ulong)(k << 16 | i) * 0x9E3779B97F4A7C15);
                BitConverter.TryWriteBytes(b.AsSpan(i * size + 8), Put(6, kinds[k][i].Rs));
                for (var j = 0; j < stages.Length; j++) BitConverter.TryWriteBytes(b.AsSpan(i * size + 4 * stages[j].Field), Put(stages[j].Section, kinds[k][i].Stages[j]));
            }
        }
        tables[6] = [.. BitConverter.GetBytes(1), .. BitConverter.GetBytes(0x00030006)];
        counts[6] = 2;
        var body = new MemoryStream();
        var tableOff = new uint[10];
        for (var t = 0; t < 10; t++) { tableOff[t] = (uint)body.Length; body.Write(tables[t] ?? []); }
        var secOff = new long[7];
        for (var s = 0; s < 7; s++) { secOff[s] = body.Length; body.Write(secs[s].ToArray()); }
        var h = new byte[0xC0];
        "PSPC"u8.CopyTo(h);
        BitConverter.TryWriteBytes(h.AsSpan(4), version);
        BitConverter.TryWriteBytes(h.AsSpan(0x10), (ulong)body.Length);
        for (var s = 0; s < 6; s++)
        {
            BitConverter.TryWriteBytes(h.AsSpan(0x18 + 8 * s), (ulong)secOff[s]);
            BitConverter.TryWriteBytes(h.AsSpan(0x48 + 4 * s), (uint)secs[s].Length);
        }
        for (var t = 0; t < 10; t++) { BitConverter.TryWriteBytes(h.AsSpan(0x60 + 8 * t), counts[t]); BitConverter.TryWriteBytes(h.AsSpan(0x64 + 8 * t), tableOff[t]); }
        BitConverter.TryWriteBytes(h.AsSpan(0xB0), (ulong)secOff[6]);
        BitConverter.TryWriteBytes(h.AsSpan(0xB8), (ulong)secs[6].Length);
        return [.. h, .. body.ToArray()];
    }

    /// <summary>Byte offset in the file of an entry's u32 field (table 0 first in the body).</summary>
    static int Field(int table, int entry, int field, byte[] file) =>
        0xC0 + (int)BitConverter.ToUInt32(file, 0x64 + 8 * table) + entry * PspcReader.Kinds.First(k => k.Table == table).Size + 4 * field;

    static Game Write(string name, byte[] file)
    {
        var dir = Ff7.TempDir(name);
        File.WriteAllBytes(Path.Combine(dir, "ffxvi.pspc"), file);
        return new Game("test:" + name, name, Store.Other, dir, Path.Combine(dir, "ffxvi.exe"));
    }

    sealed class Log(List<string> lines) : IProgress<string> { public void Report(string value) => lines.Add(value); }

    [Fact]
    public void IndexesEveryEntryAsAPipelineWithItsRootSignature()
    {
        var d = Data.Value;
        var reader = new PspcReader();
        var engine = new EngineReaders((PspcReader.Family, reader), (CarvedReader.Family, new CarvedReader())).Detect(d.Game)!;
        Assert.Equal(new EngineInfo(PspcReader.Family, "0x0300000A", null, "D3D12", false, null, ShipsRootSignatures: true), engine);
        var log = new List<string>();
        var index = reader.Index(d.Game, engine, new Log(log), CancellationToken.None);
        Assert.Equal(10, index.Shaders.Count);
        Assert.All(index.Shaders.Values, s => Assert.Null(s.RootSignature));
        Assert.Equal([Stage.Vertex, Stage.Pixel, Stage.Geometry, Stage.Hull, Stage.Domain, Stage.Compute],
            new[] { d.Vs1, d.Ps1, d.Gs, d.Hs, d.Ds, d.Cs1 }.Select(b => index.Shaders[Sha(b)].Stage));
        Assert.All(index.Maps, m => Assert.True(m.IsPipeline));
        // each distinct (stages, root signature) once: the repeated VS1+PS1 under A is one map, under B another
        Assert.Equal(new[]
            {
                (Sha(d.A), new[] { Sha(d.Vs1), Sha(d.Ps1) }), (Sha(d.B), [Sha(d.Vs1), Sha(d.Ps1)]), (Sha(d.A), [Sha(d.Vs2), Sha(d.Ps2)]),
                (Sha(d.A), [Sha(d.Vs1), Sha(d.Gs), Sha(d.Ps1)]), (Sha(d.A), [Sha(d.VsTess), Sha(d.Hs), Sha(d.Ds), Sha(d.Ps1)]),
                (Sha(d.C), [Sha(d.Cs1)]), (Sha(d.C), [Sha(d.Cs2)]),
            }.Select(x => $"{x.Item1}:{string.Join(',', x.Item2)}"),
            index.Maps.Select(m => $"{m.RootSignature}:{string.Join(',', m.Shaders)}"));
        Assert.Equal(["D3D12"], index.Platforms);
        Assert.Contains(log, l => l.Contains("8 pipelines (4 VS+PS, 1 VS+GS+PS, 1 VS+HS+DS+PS, 2 CS) -> 7 distinct") && l.Contains("3 root signatures"));
        Assert.Equal(index.ContentHash, new PspcReader().Index(d.Game, engine, null, CancellationToken.None).ContentHash);

        // shaders and root signatures alike, sliced to their containers
        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(d.Game, engine, new HashSet<string> { Sha(d.Ds), Sha(d.B), Sha(d.C), new('0', 40) }, (h, b) => got.Add(h, b), CancellationToken.None);
        foreach (var b in new[] { d.Ds, d.B, d.C }) Assert.Equal(b, got[Sha(b)]);
        Assert.Equal(3, got.Count);
        got.Clear();
        new PspcReader().ReadShaders(d.Game, engine, new HashSet<string> { Sha(d.Hs) }, (h, b) => got.Add(h, b), CancellationToken.None); // no Index in this reader: it indexes first
        Assert.Equal(d.Hs, got[Sha(d.Hs)]);
    }

    /// <summary>NVIDIA's cache is state-independent: the shipped root signatures plan without a recording. AMD's keys on
    /// state the file doesn't give: it needs a recording, and says why. An engine without the flag keeps its old answer.</summary>
    [Fact]
    public void ReadyWithoutARecordingOnNvidiaOnly()
    {
        var d = Data.Value;
        var engine = new PspcReader().Detect(d.Game)!;
        var planner = new Planner();
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.NoRecording), planner.Check(d.Game, engine, null, Ff7.Nvidia));
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.NoRecording), planner.Check(d.Game, engine, null, Ff7.Nvidia with { PerStageCache = true }));
        Assert.Equal(new PlanCheck(Readiness.NeedsRecording, Planner.Record + Planner.StateFromRecording), planner.Check(d.Game, engine, null, Ff7.Amd));
        Assert.Equal(new PlanCheck(Readiness.Unsupported, "not supported on this GPU yet"), planner.Check(d.Game, engine, null, Ff7.Nvidia with { CacheKeyedByExeName = false }));
        var carved = new EngineInfo(CarvedReader.Family, "DXBC", null, "D3D12", false, null);
        Assert.Equal(new PlanCheck(Readiness.NeedsRecording, Planner.Record), planner.Check(d.Game, carved, null, Ff7.Nvidia));
        Assert.Equal(new PlanCheck(Readiness.NeedsRecording, Planner.Record), planner.Check(d.Game, carved, null, Ff7.Amd));
    }

    /// <summary>A shader drawn under two root signatures compiles under both (NVIDIA's unit is shader + root signature): every
    /// shipped pipeline plans with its own as whole pipelines, and per stage every shader under each of its root signatures;
    /// the root signatures come from the file at materialize time, none is generated.</summary>
    [Fact]
    public void PlansEveryPipelineUnderItsOwnRootSignature()
    {
        var d = Data.Value;
        var reader = new PspcReader();
        var engine = reader.Detect(d.Game)!;
        var index = reader.Index(d.Game, engine, null, CancellationToken.None);
        string T(byte[] rs, params byte[][] st) => PsoDb.Tuple(Sha(rs), st.Select(b => new KeyValuePair<int, string>((int)index.Shaders[Sha(b)].Stage, Sha(b))));
        var want = new HashSet<string>
        {
            T(d.A, d.Vs1, d.Ps1), T(d.B, d.Vs1, d.Ps1), T(d.A, d.Vs2, d.Ps2), T(d.A, d.Vs1, d.Gs, d.Ps1), T(d.A, d.VsTess, d.Hs, d.Ds, d.Ps1), T(d.C, d.Cs1), T(d.C, d.Cs2),
        };
        foreach (var caps in new[] { Ff7.Nvidia, Ff7.Nvidia with { PerStageCache = true } })
        {
            var dir = Ff7.TempDir($"pspc-plan-{caps.PerStageCache}");
            var planner = new Planner();
            var plan = planner.Build(d.Game, engine, index, null, caps, dir, null, CancellationToken.None);
            var body = PlanFile.Read(plan.FilePath).Records.ToList();
            Assert.DoesNotContain(body, r => r.Tag == 'B'); // the game's own: read from the file, never in the plan
            var psos = body.Where(r => r.Tag == 'S').Select(PsoDb.Parse).Select(p => (p.Rs, p.Stages))
                .Concat(body.Where(r => r.Tag == 'P').Select(r => PsoDb.ParseItem(r.Payload)).Select(i => (i.Rs, i.Stages))).ToList();
            if (caps.PerStageCache) // the cover: each shader under each of its root signatures once
                Assert.Equal(index.Maps.SelectMany(m => m.Shaders.Select(s => $"{m.RootSignature}:{s}")).ToHashSet(), psos.SelectMany(p => p.Stages.Values.Select(s => $"{p.Rs}:{s}")).ToHashSet());
            else Assert.Equal(want, psos.Select(p => PsoDb.Tuple(p.Rs, p.Stages)).ToHashSet());
            Assert.Contains(psos, p => p.Rs == Sha(d.B) && p.Stages.ContainsValue(Sha(d.Vs1)));
            Assert.Equal((7L, 0L, 0L), (plan.Stats.StageSets, plan.Stats.LeftOut, plan.Stats.Uncovered));
            var work = Path.Combine(dir, "work");
            planner.Materialize(plan, d.Game, engine, reader, null, work, CancellationToken.None);
            Ff7.CheckWarmReady(work);
        }
    }

    [Fact]
    public void AFileThatIsntAPipelineListIsNotDetected()
    {
        var d = Data.Value;
        var file = File.ReadAllBytes(d.File);
        file[0] = (byte)'X';
        Assert.Null(new PspcReader().Detect(Write("pspc-magic", file)));
        Assert.Null(new PspcReader().Detect(Write("pspc-tiny", "PSPC"u8.ToArray())));
        Assert.Null(new PspcReader().Detect(d.Game with { ExePath = Path.Combine(d.Dir, "missing", "ffxvi.exe") }));
        var empty = Ff7.TempDir("pspc-none");
        Assert.Null(new PspcReader().Detect(new Game("test:none", "none", Store.Other, empty, Path.Combine(empty, "ffxvi.exe"))));
    }

    /// <summary>A pipeline list this reader can't take says why, and is never indexed: another version, a truncated or
    /// damaged file, a table bigger than the cap, a first pipeline that doesn't read.</summary>
    [Fact]
    public void AListItCantTakeIsUnsupportedWithTheReason()
    {
        var d = Data.Value;
        var good = File.ReadAllBytes(d.File);
        string? Why(string name, byte[] file) => new PspcReader().Detect(Write(name, file)) is { } e ? e.Unsupported : "not detected";

        Assert.Equal("ffxvi.pspc: pipeline list version 0x0300000B isn't supported yet", Why("pspc-version", Pspc(d.Entries, 0x0300000B)));
        Assert.Contains("damaged or truncated", Why("pspc-truncated", good[..^100]));
        var cut = good[..^100];
        BitConverter.TryWriteBytes(cut.AsSpan(0x10), (ulong)(cut.Length - 0xC0)); // the size matches, a section runs past the end
        Assert.Contains("damaged or truncated", Why("pspc-section", cut));
        var huge = (byte[])good.Clone();
        BitConverter.TryWriteBytes(huge.AsSpan(0x60), PspcReader.MaxEntries + 1);
        Assert.Contains("damaged or truncated", Why("pspc-count", huge));
        var off = (byte[])good.Clone();
        BitConverter.TryWriteBytes(off.AsSpan(0x18), ulong.MaxValue - 4); // a section offset that wraps
        Assert.Contains("damaged or truncated", Why("pspc-wrap", off));
        var first = (byte[])good.Clone();
        BitConverter.TryWriteBytes(first.AsSpan(Field(0, 0, 7, first)), 0xFFFFFF00u);
        Assert.Equal("ffxvi.pspc: its first pipeline doesn't read as this layout", Why("pspc-first", first));
        Assert.Throws<InvalidDataException>(() => new PspcReader().Index(Write("pspc-first-index", first), new EngineInfo(PspcReader.Family, "0x0300000A", null, "D3D12", false, null, ShipsRootSignatures: true), null, CancellationToken.None));
    }

    /// <summary>An entry that doesn't read (an offset past its section, a root signature that isn't one, a container of another
    /// stage in a slot) is left out with a count; the others index.</summary>
    [Fact]
    public void EntriesThatDontReadAreLeftOut()
    {
        var d = Data.Value;
        var file = Pspc([[new(d.A, d.Vs1, d.Ps1), new(d.A, d.Vs2, d.Ps2), new(d.B, d.Vs2, d.Ps1), new(d.A, d.Ps2, d.Ps1)], [], [], [new(d.C, d.Cs1), new(d.Vs1, d.Cs2)]]);
        BitConverter.TryWriteBytes(file.AsSpan(Field(0, 1, 7, file)), 0x7FFFFFF0u);   // VS2+PS2: the PS offset is past the section
        BitConverter.TryWriteBytes(file.AsSpan(Field(0, 2, 2, file)), 3u);            // B: not at a root signature
        var game = Write("pspc-entries", file);
        var reader = new PspcReader();
        var engine = reader.Detect(game)!;
        Assert.Null(engine.Unsupported);
        var log = new List<string>();
        var index = reader.Index(game, engine, new Log(log), CancellationToken.None);
        Assert.Equal([$"{Sha(d.A)}:{Sha(d.Vs1)},{Sha(d.Ps1)}", $"{Sha(d.C)}:{Sha(d.Cs1)}"], index.Maps.Select(m => $"{m.RootSignature}:{string.Join(',', m.Shaders)}"));
        Assert.Contains(log, l => l.Contains("3 left out: a container that doesn't read") && l.Contains("1 left out: a shader of another stage than its slot"));
    }

    /// <summary>Random damage to the header and the tables never throws out of Detect, and out of Index only as
    /// InvalidDataException (no usable pipeline left).</summary>
    [Fact]
    public void DamagedFilesNeverThrowOutOfTheReader()
    {
        var d = Data.Value;
        var good = File.ReadAllBytes(d.File);
        var tablesEnd = 0xC0 + (int)BitConverter.ToUInt64(good, 0x18);
        var rnd = new Random(1234);
        var dir = Ff7.TempDir("pspc-fuzz");
        var game = new Game("test:fuzz", "fuzz", Store.Other, dir, Path.Combine(dir, "ffxvi.exe"));
        int detected = 0, indexed = 0;
        for (var i = 0; i < 400; i++)
        {
            var f = (byte[])good.Clone();
            for (var n = rnd.Next(1, 6); n > 0; n--)
            {
                var at = rnd.Next(0x10, tablesEnd);
                f[at] = rnd.Next(3) == 0 ? (byte)0xFF : (byte)rnd.Next(256);
            }
            File.WriteAllBytes(Path.Combine(dir, "ffxvi.pspc"), f);
            var reader = new PspcReader();
            var engine = reader.Detect(game);
            if (engine is not { Unsupported: null }) continue;
            detected++;
            try { reader.Index(game, engine, null, CancellationToken.None); indexed++; }
            catch (InvalidDataException) { }
        }
        Assert.True(detected > 0 && indexed > 0);
    }
}
