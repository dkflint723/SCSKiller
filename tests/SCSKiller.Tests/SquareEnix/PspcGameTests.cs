using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.SquareEnix;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.SquareEnix;

/// <summary>FINAL FANTASY XVI on this machine (Steam; read-only; skipped when absent): its ffxvi.pspc found before the carver,
/// every pipeline of it indexed with its root signature, planned per stage on NVIDIA without a recording.</summary>
[Trait("Needs", "Game")]
public class PspcGameTests(ITestOutputHelper output)
{
    [Fact]
    public void PlansFinalFantasyXviWithoutARecording()
    {
        var dir = TestEnv.GameDir("FINAL FANTASY XVI");
        var game = new Game("steam:2515020", "FINAL FANTASY XVI", Store.Steam, dir, Path.Combine(dir, "ffxvi.exe"));
        if (!File.Exists(Path.Combine(dir, "ffxvi.pspc"))) return;
        var reader = new PspcReader();
        var sw = Stopwatch.StartNew();
        var engine = new EngineReaders((PspcReader.Family, reader), (CarvedReader.Family, new CarvedReader())).Detect(game)!;
        output.WriteLine($"detect {sw.Elapsed.TotalSeconds:F2}s: {engine}; anti-cheat {GameFiles.DetectAntiCheat(game)}");
        Assert.Equal(new EngineInfo(PspcReader.Family, "0x0300000A", null, "D3D12", false, null, ShipsRootSignatures: true), engine);
        var nvidia = Ff7.Nvidia with { PerStageCache = true };
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.NoRecording), new Planner().Check(game, engine, null, nvidia));
        Assert.Equal(Readiness.NeedsRecording, new Planner().Check(game, engine, null, Ff7.Amd).Readiness);

        sw.Restart();
        var index = reader.Index(game, engine, new Progress<string>(output.WriteLine), CancellationToken.None);
        var rootSigs = index.Maps.Select(m => m.RootSignature).Distinct().Count();
        var units = index.Maps.SelectMany(m => m.Shaders.Select(s => (s, m.RootSignature))).Distinct().Count();
        output.WriteLine($"index {sw.Elapsed.TotalSeconds:F1}s: {index.Shaders.Count} shaders, {index.Maps.Count} pipelines, {rootSigs} root signatures, {units} shader x root signature units");
        // the 2025-06-27 build: 83,444 shaders, 63,645 distinct pipelines of 78,020 entries, 1,361 root signatures, 86,089 units
        Assert.InRange(index.Shaders.Count, 75_000, 95_000);
        Assert.InRange(index.Maps.Count, 57_000, 70_000);
        Assert.InRange(rootSigs, 1_200, 1_500);
        Assert.All(index.Maps, m => Assert.True(m.IsPipeline && m.RootSignature != null));

        sw.Restart();
        var plan = new Planner().Build(game, engine, index, null, nvidia, Ff7.TempDir("pspc-ffxvi"), new Progress<string>(output.WriteLine), CancellationToken.None);
        output.WriteLine($"plan {sw.Elapsed.TotalSeconds:F1}s, {new FileInfo(plan.FilePath).Length / 1024} KiB: {plan.Stats}");
        Assert.Equal((0L, 0L), (plan.Stats.Uncovered, plan.Stats.LeftOut));
        Assert.InRange(plan.Stats.Generated, 50_000, 65_000);
    }
}
