using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.Northlight;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Carved;
using SCSKiller.Tests.Planning;

namespace SCSKiller.Tests.Northlight;

/// <summary>The CONTROL Resonant reader on a synthetic install: effect files of layout 'O' laid out as the game's, with
/// shaders compiled here (<see cref="Hlsl"/>).</summary>
public class Northlight2ReaderTests
{
    const string VsSrc = "float4 main(float3 p : POSITION, float2 uv : TEXCOORD0, out float2 ouv : TEXCOORD0) : SV_Position { ouv = uv; return float4(p * {0}.0, 1); }";
    const string VsOffSrc = "void main(float3 p : POSITION, out float4 pos : SV_Position, out float4 x : COLOR0) { pos = float4(p, 1); x = pos; }";   // feeds no TEXCOORD0
    const string PsSrc = """
        Texture2D t : register(t0); SamplerState s : register(s0); Texture2D bindless[] : register(t0, space1);
        float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return t.Sample(s, uv) * {0}.0 + bindless[(uint)uv.x].Load(int3(0, 0, 0)); }
        """;
    const string CsSrc = "RWByteAddressBuffer u : register(u0); [numthreads(64, 1, 1)] void main(uint i : SV_DispatchThreadID) { u.Store(i * 4, i); }";
    const int Vs = 0, Ps = 1, Cs = 2, Ms = 3, Lib = 4;

    sealed class Fixture
    {
        public readonly string Dir = Ff7.TempDir("northlight2-synthetic");
        public readonly byte[][] Vs = [.. Enumerable.Range(1, 2).Select(i => Hlsl.Compile(VsSrc.Replace("{0}", $"{i}"), "main", "vs_5_1"))];
        public readonly byte[][] Ps = [.. Enumerable.Range(1, 2).Select(i => Hlsl.Compile(PsSrc.Replace("{0}", $"{i}"), "main", "ps_5_1", Hlsl.UnboundedTables))];
        public readonly byte[] VsOff = Hlsl.Compile(VsOffSrc, "main", "vs_5_1"), Cs = Hlsl.Compile(CsSrc, "main", "cs_5_1");
        public readonly byte[] Lib = NorthlightReaderTests.Library((10, "closestHitMain", 32), (7, "rayGen", 0));
        public Game Game => new("test:northlight2", "northlight2", Store.Other, Dir, Path.Combine(Dir, "CONTROLResonant.exe"));
        public string Effects => Path.Combine(Dir, @"data\shaders\build\pc_dx12");

        public Fixture()
        {
            Directory.CreateDirectory(Effects);
            // VS1+PS1 in two techniques (one pipeline), VS2+PS2, the CS alone, VsOff+PS1 (doesn't link: left out), the library
            File.WriteAllBytes(Path.Combine(Effects, "copy.binrfx"), Effect([(Vs[0], 0), (Vs[1], 0), (VsOff, 0), (Ps[0], 1), (Ps[1], 1), (Cs, 2)],
                [[0, 3, -1, -1, -1], [1, 4, -1, -1, -1], [2, 3, -1, -1, -1]], [[-1, -1, 5, -1, -1], [0, 3, -1, -1, -1]]));
            File.WriteAllBytes(Path.Combine(Effects, "rt_ao.binrfx"), Effect([(Lib, 4)], [[-1, -1, -1, -1, 0]]));
            File.WriteAllBytes(Path.Combine(Effects, "cloth.binrfx"), Effect([]));   // a material's effect: no shader
            File.WriteAllBytes(Path.Combine(Effects, "notes.binrfx"), "not an effect"u8.ToArray());
            File.WriteAllBytes(Path.Combine(Effects, "half.binrfx"), [.. "RFX "u8, .. BitConverter.GetBytes((int)'O'), .. BitConverter.GetBytes(4), .. "copy"u8]);   // cut after its name: skipped
        }
    }

    static readonly Lazy<Fixture> Data = new(() => new Fixture());
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));
    static ulong Id(int i) => 0x9E3779B97F4A7C15UL * (ulong)(i + 1);

    /// <summary>A layout 'O' effect file: header, then per group (VS, PS, CS, MS, library) a count and its shaders, each its
    /// entry point, size, container, some reflection, an 8-byte id and a u32 (9 on every third, as the game's vary); then the
    /// technique table: per technique its name and entries of 5 ids and a permutation key, its hash, and the include list.
    /// <paramref name="shaders"/> in group order; each technique's entries index them per group (-1: none).</summary>
    internal static byte[] Effect((byte[] C, int Group)[] shaders, params int[][][] techniques)
    {
        var s = new MemoryStream();
        var w = new BinaryWriter(s);
        void Str(string v) { w.Write(v.Length); w.Write(Encoding.ASCII.GetBytes(v)); }
        w.Write("RFX "u8); w.Write((int)'O'); Str("copy");
        for (var g = 0; g < 5; g++)
        {
            w.Write(shaders.Count(x => x.Group == g));
            for (var i = 0; i < shaders.Length; i++)
            {
                if (shaders[i].Group != g) continue;
                Str(g == Lib ? "" : $"main{i}"); w.Write((long)shaders[i].C.Length); w.Write(shaders[i].C);
                w.Write(1); w.Write((byte)0); w.Write(-1); w.Write(0); Str("g_tInput"); w.Write(new byte[] { 0, 1, 4 });   // reflection
                w.Write(Id(i)); w.Write(i % 3 == 2 ? 9 : 0);
            }
        }
        w.Write(techniques.Length);
        for (var t = 0; t < techniques.Length; t++)
        {
            Str($"technique{t}"); w.Write(techniques[t].Length);
            var key = 0;
            foreach (var e in techniques[t])
            {
                foreach (var i in e) w.Write(i < 0 ? 0UL : Id(i));
                w.Write(key += 2);
            }
            w.Write(new byte[12]); w.Write(Id(1000 + t));
        }
        Str("copy.rfx"); w.Write(new byte[16]); Str("include/sys.h");
        return s.ToArray();
    }

    /// <summary>A container of program type <paramref name="kind"/> and nothing else (a mesh shader: no compiler for one here).</summary>
    static byte[] Program(int kind, int seed)
    {
        byte[] dxil = [.. "DXIL"u8, .. BitConverter.GetBytes(8), .. BitConverter.GetBytes(kind << 16 | 0x65), .. BitConverter.GetBytes(seed)];
        return [.. "DXBC"u8, .. new byte[16], .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(36 + dxil.Length), .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(36), .. dxil];
    }

    /// <summary>A DXIL container as the game ships (SM 6.x): the program header (<paramref name="kind"/> &lt;&lt; 16 | major
    /// &lt;&lt; 4 | minor of <paramref name="sm"/>) and 32-byte signature parts of (name, index, system value, register, mask), float.</summary>
    static byte[] Dxil(int kind, int sm, params (string Part, (string Name, int Index, int Sys, int Reg, byte Mask)[] Elems)[] sigs)
    {
        var parts = new List<(string Fourcc, byte[] Data)>();
        foreach (var (part, elems) in sigs)
        {
            var d = new byte[8 + 32 * elems.Length];
            var names = new List<byte>();
            BitConverter.GetBytes(elems.Length).CopyTo(d, 0);
            BitConverter.GetBytes(8).CopyTo(d, 4);
            for (var i = 0; i < elems.Length; i++)
            {
                var (name, idx, sys, reg, mask) = elems[i];
                foreach (var (at, v) in new[] { (4, d.Length + names.Count), (8, idx), (12, sys), (16, 3), (20, reg) }) BitConverter.GetBytes(v).CopyTo(d, 8 + 32 * i + at);
                d[8 + 32 * i + 24] = mask;
                names.AddRange([.. Encoding.ASCII.GetBytes(name), 0]);
            }
            parts.Add((part, [.. d, .. names]));
        }
        parts.Add(("DXIL", BitConverter.GetBytes(kind << 16 | sm)));
        var b = new List<byte>([.. "DXBC"u8, .. new byte[16], .. BitConverter.GetBytes(1), .. new byte[4], .. BitConverter.GetBytes(parts.Count)]);
        var pos = 32 + 4 * parts.Count;
        foreach (var p in parts) { b.AddRange(BitConverter.GetBytes(pos)); pos += 8 + p.Data.Length; }
        foreach (var p in parts) b.AddRange([.. Encoding.ASCII.GetBytes(p.Fourcc), .. BitConverter.GetBytes(p.Data.Length), .. p.Data]);
        var c = b.ToArray();
        BitConverter.GetBytes(c.Length).CopyTo(c, 24);
        return c;
    }

    [Fact]
    public void ReadsTheShadersAndTechniqueEntriesOfAnEffectFile()
    {
        var d = Data.Value;
        var ms = Program(13, 1);
        // no VS group, a mesh shader after an empty CS group, an entry with a VS, an MS and their PS
        var f = Effect([(d.Ps[0], 1), (d.Ps[1], 1), (ms, 3), (d.Lib, 4)], [[-1, 0, -1, 2, -1], [-1, 1, -1, 2, -1]], [[-1, -1, -1, -1, 3], [-1, 0, -1, 2, -1]]);
        var e = Northlight2Reader.Parse(f, default)!;
        Assert.Equal([Ps, Ps, Ms, Lib], e.Shaders.Select(s => s.Group));
        Assert.Equal(Sha(ms), Sha(f.AsSpan(e.Shaders[2].Offset, e.Shaders[2].Size).ToArray()));
        Assert.Equal(["-1,0,-1,2,-1", "-1,1,-1,2,-1", "-1,-1,-1,-1,3"], e.Entries.Select(x => string.Join(',', x)));   // distinct, in file order

        Assert.Empty(Northlight2Reader.Parse(Effect([]), default)!.Shaders);
        Assert.Null(Northlight2Reader.Parse("not an effect"u8.ToArray(), default));
        var aw2 = (byte[])f.Clone();
        aw2[4] = (byte)':';   // Alan Wake 2's layout
        Assert.Null(Northlight2Reader.Parse(aw2, default));
        Assert.Null(Northlight2Reader.Parse(Effect([(d.Ps[0], 1), (d.Vs[0], 1)], [[-1, 0, -1, -1, -1]]), default));   // a VS in the PS group: not this layout
        var miscounted = Effect([(d.Ps[0], 1), (d.Ps[1], 1)], [[-1, 0, -1, -1, -1]]);
        miscounted[miscounted.AsSpan().IndexOf("copy"u8) + 8]++;   // the PS group's count, 2 -> 3
        Assert.Null(Northlight2Reader.Parse(miscounted, default));
        var noTable = Effect([(d.Ps[0], 1), (d.Ps[1], 1)], [[-1, 0, -1, -1, -1]]);
        Assert.Null(Northlight2Reader.Parse(noTable[..(noTable.AsSpan().LastIndexOf("technique0"u8) - 4)], default));   // the last shader's id: nowhere
        // truncated (an update writing it): the header's name with no count after it is no effect; cut before its technique
        // table, at most an empty one (a zero count is all it reads); cut anywhere, Parse doesn't throw
        Assert.Null(Northlight2Reader.Parse([.. "RFX "u8, .. BitConverter.GetBytes((int)'O'), .. BitConverter.GetBytes(4), .. "copy"u8], default));
        Assert.Null(Northlight2Reader.Parse([.. "RFX "u8, .. BitConverter.GetBytes((int)'O'), .. BitConverter.GetBytes(4), .. "copy"u8, 0, 0], default));
        var table = f.AsSpan().IndexOf("technique0"u8);
        for (var n = 0; n < f.Length; n++)
            if (Northlight2Reader.Parse(f[..n], default) is { } cut) Assert.True(n > table || cut is { Shaders: [], Entries: [] }, $"cut at {n}");
    }

    [Fact]
    public void IndexesEachTechniquePipeline()
    {
        var d = Data.Value;
        var reader = new Northlight2Reader();
        var engine = reader.Detect(d.Game)!;
        Assert.Equal(new EngineInfo(Northlight2Reader.Family, "DX12", null, "D3D12", false, null), engine);
        Assert.Null(new NorthlightReader().Detect(d.Game));   // not Control's pc_dxil
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.Untested), new Planner().Check(d.Game, engine, null, Ff7.Nvidia));
        Assert.Equal(RootSig.Rule.Northlight2, RootSig.RuleFor(engine));
        Assert.False(RootSig.Verified(engine));
        var log = new List<string>();
        var index = reader.Index(d.Game, engine, new Log(log.Add), CancellationToken.None);
        Assert.Equal(7, index.Shaders.Count);
        Assert.All(index.Shaders.Values, s => Assert.Null(s.RootSignature));
        Assert.Equal([[Sha(d.Vs[0]), Sha(d.Ps[0])], [Sha(d.Vs[1]), Sha(d.Ps[1])], [Sha(d.Cs)]],
            index.Maps.Where(m => m.IsPipeline).Select(m => m.Shaders.ToArray()));   // each distinct once; the unlinked one left out
        var libs = Assert.Single(index.Maps, m => !m.IsPipeline);
        Assert.Equal((Northlight2Reader.Libraries, Stage.Library), (libs.Library, index.Shaders[Assert.Single(libs.Shaders)].Stage));
        Assert.Contains(log, l => l.Contains("1 left out: their VS or MS doesn't feed their PS"));

        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(d.Game, engine, new HashSet<string> { Sha(d.Ps[1]), Sha(d.Lib), new('0', 40) }, (h, b) => got.Add(h, b), CancellationToken.None);
        Assert.Equal(d.Ps[1], got[Sha(d.Ps[1])]);
        Assert.Equal(d.Lib, got[Sha(d.Lib)]);
        Assert.Equal(2, got.Count);
        Assert.Null(reader.Detect(d.Game with { InstallDir = Path.Combine(d.Dir, "missing") }));
    }

    sealed class Log(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }

    /// <summary>The engine's three root signatures, serialized, are byte for byte the ones CONTROL Resonant creates (its
    /// recording on the PC this was measured on: 677 VS+PS, 111 MS+PS and 594 compute PSOs, all with these).</summary>
    [Fact]
    public void BuildsTheRootSignaturesTheGameCreates()
    {
        ShaderInfo S(Stage s) => new("", s, "", 0, new(0, 0, 0, 0), [], [], []);
        string Built(params Stage[] st) => PsoDb.Hex(SHA1.HashData(RootSig.Serialize(RootSig.Build(RootSig.Rule.Northlight2, st.ToDictionary(s => s, S), false), [])));
        Assert.Equal("f6b967e2bce45fc1ebcb49bbc36ca19f2c54e486", Built(Stage.Vertex, Stage.Pixel));
        Assert.Equal("f6b967e2bce45fc1ebcb49bbc36ca19f2c54e486", Built(Stage.Vertex));
        Assert.Equal("c2401a34ac324e3dab7a6c2b1ca4ef3c83116bdc", Built(Stage.Mesh, Stage.Pixel));
        Assert.Equal("8492d3d1cc34576ffa8b33c7a02e4550dda91288", Built(Stage.Compute));
        Assert.Empty(RootSig.StaticSamplers(RootSig.Rule.Northlight2));
        foreach (var st in new Stage[][] { [Stage.Vertex, Stage.Mesh, Stage.Pixel], [Stage.Vertex, Stage.Geometry, Stage.Pixel], [Stage.Pixel], [Stage.Amplification, Stage.Mesh, Stage.Pixel] })
            Assert.Throws<RootSig.SerializeException>(() => RootSig.Build(RootSig.Rule.Northlight2, st.ToDictionary(s => s, S), false));
    }

    /// <summary>Planned without a recording on NVIDIA: each technique pipeline once with the engine's root signatures, every
    /// shader's bindings covered; the library isn't (no collection rule for this engine yet).</summary>
    [Fact]
    public void PlansEachPipelineWithTheEnginesRootSignatures()
    {
        var d = Data.Value;
        var reader = new Northlight2Reader();
        var engine = reader.Detect(d.Game)!;
        var index = reader.Index(d.Game, engine, null, CancellationToken.None);
        var dir = Ff7.TempDir("northlight2-plan");
        var planner = new Planner();
        var plan = planner.Build(d.Game, engine, index, null, Ff7.Nvidia, dir, null, CancellationToken.None);
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        var psos = body.Where(r => r.Tag == 'S').Select(r => PsoDb.Tuple(PsoDb.Parse(r).Rs, PsoDb.Parse(r).Stages))
            .Concat(body.Where(r => r.Tag == 'P').Select(r => PsoDb.ParseItem(r.Payload)).Select(i => PsoDb.Tuple(i.Rs, i.Stages))).ToHashSet();
        const string gfx = "f6b967e2bce45fc1ebcb49bbc36ca19f2c54e486", cs = "8492d3d1cc34576ffa8b33c7a02e4550dda91288";
        string T(string rs, params (Stage S, byte[] B)[] st) => PsoDb.Tuple(rs, st.Select(x => new KeyValuePair<int, string>((int)x.S, Sha(x.B))));
        Assert.Equal(new HashSet<string> { T(gfx, (Stage.Vertex, d.Vs[0]), (Stage.Pixel, d.Ps[0])), T(gfx, (Stage.Vertex, d.Vs[1]), (Stage.Pixel, d.Ps[1])), T(cs, (Stage.Compute, d.Cs)) }, psos);
        Assert.Equal(new[] { gfx, cs }.Order(), body.Where(r => r.Tag == 'B').Select(r => PsoDb.Hex(r.Payload.AsSpan(0, 20))).Order());
        Assert.Equal((0L, 0L, 1L, 1L), (plan.Stats.Uncovered, plan.Stats.LeftOut, plan.Stats.RtLibraries, plan.Stats.RtUncovered));
        var work = Path.Combine(dir, "work");
        planner.Materialize(plan, d.Game, engine, reader, null, work, CancellationToken.None);
        Ff7.CheckWarmReady(work);
    }

    /// <summary>DXIL, as the game ships: VS+PS, MS+PS and CS entries are each a pipeline, planned with the root signature of
    /// its kind (MS+PS: the mesh one); an MS+PS pair is kept as the technique names it, also one Planner.MeshFeeds rejects.</summary>
    [Fact]
    public void IndexesAndPlansDxilVertexMeshAndComputePipelines()
    {
        var dir = Ff7.TempDir("northlight2-dxil");
        var effects = Directory.CreateDirectory(Path.Combine(dir, @"data\shaders\build\pc_dx12")).FullName;
        (string, int, int, int, byte) Pos = ("SV_Position", 0, 1, 0, 0xF);
        (string, int, int, int, byte) Uv(byte mask) => ("TEXCOORD", 0, 0, 1, mask);
        var vs = Dxil(1, 0x66, ("OSG1", [Pos, Uv(3)]));
        var ps = Dxil(0, 0x66, ("ISG1", [Pos, Uv(3)]), ("OSG1", [("SV_Target", 0, 64, 0, 0xF)]));
        var cs = Dxil(5, 0x66);
        var ms = Dxil(13, 0x65, ("OSG1", [Pos, Uv(3)]));
        var msWide = Dxil(13, 0x65, ("OSG1", [Pos, Uv(7)]));   // TEXCOORD0.xyz to the PS's .xy: not the exact mask MeshFeeds asks for
        File.WriteAllBytes(Path.Combine(effects, "deferred.binrfx"), Effect([(vs, Vs), (ps, Ps), (cs, Cs), (ms, Ms), (msWide, Ms)],
            [[0, 1, -1, -1, -1], [-1, 1, -1, 3, -1], [-1, 1, -1, 4, -1]], [[-1, -1, 2, -1, -1]]));
        var game = new Game("test:northlight2-dxil", "northlight2-dxil", Store.Other, dir, Path.Combine(dir, "CONTROLResonant.exe"));
        var reader = new Northlight2Reader();
        var engine = reader.Detect(game)!;
        var index = reader.Index(game, engine, null, CancellationToken.None);
        Assert.Equal([(Stage.Vertex, "vs_6_6"), (Stage.Pixel, "ps_6_6"), (Stage.Compute, "cs_6_6"), (Stage.Mesh, "ms_6_5"), (Stage.Mesh, "ms_6_5")],
            new[] { vs, ps, cs, ms, msWide }.Select(c => (index.Shaders[Sha(c)].Stage, index.Shaders[Sha(c)].ShaderModel)));
        Assert.True(Planner.MeshFeeds(index.Shaders[Sha(ms)], index.Shaders[Sha(ps)]));
        Assert.False(Planner.MeshFeeds(index.Shaders[Sha(msWide)], index.Shaders[Sha(ps)]));
        Assert.Equal(new[] { Sha(vs) + Sha(ps), Sha(ms) + Sha(ps), Sha(msWide) + Sha(ps), Sha(cs) }.Order(),
            index.Maps.Where(m => m.IsPipeline).Select(m => string.Concat(m.Shaders)).Order());
        Assert.DoesNotContain(index.Maps, m => !m.IsPipeline);   // every shader in an entry, no library

        var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia, Ff7.TempDir("northlight2-dxil-plan"), null, CancellationToken.None);
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        var psos = body.Where(r => r.Tag == 'S').Select(r => PsoDb.Tuple(PsoDb.Parse(r).Rs, PsoDb.Parse(r).Stages))
            .Concat(body.Where(r => r.Tag == 'P').Select(r => PsoDb.ParseItem(r.Payload)).Select(i => PsoDb.Tuple(i.Rs, i.Stages))).ToHashSet();
        const string gfx = "f6b967e2bce45fc1ebcb49bbc36ca19f2c54e486", mesh = "c2401a34ac324e3dab7a6c2b1ca4ef3c83116bdc", compute = "8492d3d1cc34576ffa8b33c7a02e4550dda91288";
        string T(string rs, params (Stage S, byte[] B)[] st) => PsoDb.Tuple(rs, st.Select(x => new KeyValuePair<int, string>((int)x.S, Sha(x.B))));
        Assert.Equal(new HashSet<string> { T(gfx, (Stage.Vertex, vs), (Stage.Pixel, ps)), T(mesh, (Stage.Mesh, ms), (Stage.Pixel, ps)),
            T(mesh, (Stage.Mesh, msWide), (Stage.Pixel, ps)), T(compute, (Stage.Compute, cs)) }, psos);
        Assert.Equal(new[] { gfx, mesh, compute }.Order(), body.Where(r => r.Tag == 'B').Select(r => PsoDb.Hex(r.Payload.AsSpan(0, 20))).Order());
        Assert.Equal((0L, 0L), (plan.Stats.Uncovered, plan.Stats.LeftOut));
    }
}
