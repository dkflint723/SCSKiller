using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Northlight;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Northlight;

/// <summary>CONTROL Resonant on this machine (Steam; read-only; skipped when absent): found before the carver, every shader of
/// its 141 effect files in a technique pipeline (930 distinct; the recording measured against them: each of its 1,082 PSOs
/// whose shaders ship in these files is one, with the root signature the rule builds), planned without a recording on NVIDIA
/// with every shader covered.</summary>
[Trait("Needs", "Game")]
public class Northlight2GameTests(ITestOutputHelper output)
{
    [Fact]
    public void PlansControlResonantWithoutARecording()
    {
        var dir = TestEnv.GameDir("CONTROL Resonant");
        var game = new Game("steam:3669870", "CONTROL Resonant", Store.Steam, dir, Path.Combine(dir, "CONTROLResonant.exe"));
        if (!Directory.Exists(Path.Combine(dir, @"data\shaders\build\pc_dx12"))) return;
        var reader = new Northlight2Reader();
        var sw = Stopwatch.StartNew();
        var engine = new EngineReaders((NorthlightReader.Family, new NorthlightReader()), (Northlight2Reader.Family, reader), (CarvedReader.Family, new CarvedReader())).Detect(game)!;
        output.WriteLine($"detect {sw.Elapsed.TotalSeconds:F1}s: {engine}; anti-cheat {GameFiles.DetectAntiCheat(game)}");
        Assert.Equal(new EngineInfo(Northlight2Reader.Family, "DX12", null, "D3D12", false, null), engine);
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.Untested), new Planner().Check(game, engine, null, Ff7.Nvidia));
        sw.Restart();
        var index = reader.Index(game, engine, new Progress<string>(output.WriteLine), CancellationToken.None);
        output.WriteLine($"index {sw.Elapsed.TotalSeconds:F1}s");
        Assert.True(index.Maps.Count(m => m.IsPipeline) >= 900);
        Assert.True(Assert.Single(index.Maps, m => m.Library == Northlight2Reader.Libraries).Shaders.Count >= 200);
        Assert.Single(index.Maps, m => !m.IsPipeline);   // no shader outside a technique
        foreach (var perStage in new[] { false, true })
        {
            sw.Restart();
            var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia with { PerStageCache = perStage }, Ff7.TempDir($"northlight2-resonant-{perStage}"),
                new Progress<string>(output.WriteLine), CancellationToken.None);
            output.WriteLine($"plan (per stage {perStage}) {sw.Elapsed.TotalSeconds:F1}s, {new FileInfo(plan.FilePath).Length / 1024} KiB: {plan.Stats}");
            Assert.Equal((0L, 0L, 3L), (plan.Stats.Uncovered, plan.Stats.LeftOut, plan.Stats.RootSignatures));
            Assert.True(plan.Stats.Generated >= 900);
        }
    }
}
