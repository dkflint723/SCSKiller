using SCSKiller.Core;
using SCSKiller.Core.Games;

namespace SCSKiller.Tests.Platform;

/// <summary>Tests that put a file straight into %TEMP% run alone, so no other test's walk can see it while it's there.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TempFolderCollection
{
    public const string Name = "Temp folder";
}

/// <summary>%TEMP% is a folder of many: thousands of other programs' leftovers, whose random names can look like anti-cheat
/// markers (*.xem). A hand-added game's upward check stops there.</summary>
[Collection(TempFolderCollection.Name)]
public class TempFolderTests
{
    [Fact]
    public void Temp_is_a_folder_of_many()
    {
        var temp = GameFiles.DirKey(Path.GetTempPath());
        Assert.True(ManualSource.IsLibrary(temp));
        Assert.Contains("many programs", ManualSource.RootProblem(temp, Path.Combine(temp, "x.exe"), []));
    }

    [Fact]
    public void A_hand_added_game_in_temp_doesnt_read_temps_own_entries()
    {
        var dir = Directory.CreateTempSubdirectory("scskiller-temp-walk-").FullName;   // %TEMP%\scskiller-temp-walk-*: the folder above the game's
        var inTemp = Path.Combine(Path.GetTempPath(), $"scskiller-temp-walk-{Guid.NewGuid():N}.xem");
        try
        {
            var install = Directory.CreateDirectory(Path.Combine(dir, "Game")).FullName;
            var exe = Path.Combine(install, "Game.exe");
            File.WriteAllBytes(exe, new byte[100]);
            var game = new Game(ManualSource.IdOf(exe), "Game", Store.Manual, install, exe);

            File.WriteAllBytes(inTemp, [0]);   // a marker-named leftover in %TEMP% itself
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game));
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game, quick: true));

            File.WriteAllBytes(Path.Combine(dir, "x3.xem"), [0]);   // the folder above the game's is still read
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(game));
        }
        finally
        {
            File.Delete(inTemp);
            Directory.Delete(dir, true);
        }
    }
}
