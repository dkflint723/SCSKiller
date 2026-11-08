using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.App;

/// <summary>This build is Kindling, an unofficial fork of SCSKiller (dkflint723's), and says so to the official servers it talks to:
/// its own User-Agent product and comment, never one of the official channels; its own anonymous upload device
/// (<see cref="UploadDevice"/>, never an official build's upload.dat); its own opt-in to sharing (<see cref="SettingsFile"/>),
/// apart from settings.json, which official builds share and rewrite; and a share only of recordings an official build
/// could have made (<see cref="UploadBlock"/>). The version is 0.0.0-dkfork.N (Directory.Build.props): the internal channel,
/// so no active check either.</summary>
public static class ForkBuild
{
    /// <summary>The name this build goes by, and its User-Agent product.</summary>
    public const string Name = "Kindling";
    public const string Product = Name;
    /// <summary>The User-Agent comment in place of the channel.</summary>
    public const string Comment = "unofficial fork of SCSKiller";
    public const string UploadDevice = "upload-fork.dat";
    public const string SettingsFile = "fork.json";
    /// <summary>games\&lt;id&gt;\: written by an earlier Kindling build, which held back a recording with a launch it couldn't
    /// vouch for (recorded without NVAPI state). This build writes no markers, but honors these until the recording is cleared.</summary>
    public const string MinimalMarker = "recorded-minimal.fork";
    /// <summary>games\&lt;id&gt;\: as <see cref="MinimalMarker"/>, for a launch recorded alongside a mod's d3d12.dll or under a ReShade a copy can't reproduce.</summary>
    public const string LayeredMarker = "recorded-layered.fork";
    /// <summary>The data folder's list of the upscaler packs (paths under the packs folder) an earlier Kindling build held back:
    /// never shared from this build while the file is there, whatever is cleared since.</summary>
    public const string PacksHeld = "packs-held.fork";

    /// <summary>The game folder holds a marker of an earlier Kindling build (<see cref="MinimalMarker"/>, <see cref="LayeredMarker"/>).</summary>
    public static bool Marked(string gameDir) => File.Exists(Path.Combine(gameDir, MinimalMarker)) || File.Exists(Path.Combine(gameDir, LayeredMarker));

    /// <summary>Removes the game folder's markers (a clear of its recording, once its packs are held).</summary>
    public static void ClearMarkers(string gameDir)
    {
        foreach (var marker in new[] { MinimalMarker, LayeredMarker }) File.Delete(Path.Combine(gameDir, marker));
    }

    /// <summary>Pack paths under the packs folder, written with either separator, compared as one.</summary>
    sealed class PackNames : IEqualityComparer<string>
    {
        public static readonly PackNames Instance = new();
        static string Norm(string s) => s.Replace('\\', '/');
        public bool Equals(string? a, string? b) => string.Equals(a is null ? null : Norm(a), b is null ? null : Norm(b), StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(string s) => StringComparer.OrdinalIgnoreCase.GetHashCode(Norm(s));
    }

    /// <summary>A set of pack paths under the packs folder (<see cref="HoldPacks"/>'s), separator-blind.</summary>
    public static HashSet<string> NewPackSet() => new(PackNames.Instance);

    static readonly object PacksLock = new();

    /// <summary>The packs in <paramref name="packsDir"/> held back for good (<see cref="PacksHeld"/> in <paramref name="listFile"/>'s
    /// place): those the file lists that are still there, and, when <paramref name="markedDirs"/> (game folders) is not empty,
    /// every pack whose header names a source in one of them (<paramref name="sourceDir"/> maps a source to its game folder, null =
    /// not a game's) or can't be read. Rewrites the file atomically when the set changed; deletes it when nothing is held; writes
    /// nothing when there is no file and nothing to hold. Paths are relative to <paramref name="packsDir"/>. Throws when the list
    /// can't be read or written.</summary>
    public static HashSet<string> HoldPacks(string packsDir, string listFile, IReadOnlyCollection<string> markedDirs, Func<string, string?> sourceDir)
    {
        var held = NewPackSet();
        var dirs = markedDirs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Filled(string source) => sourceDir(source) is { } d && dirs.Contains(d);
        lock (PacksLock)
        {
            var listed = NewPackSet();
            try { listed.UnionWith(File.ReadAllLines(listFile).Where(l => l.Length > 0)); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
            foreach (var path in Directory.Exists(packsDir) ? Directory.EnumerateFiles(packsDir, "*.pack", SearchOption.AllDirectories) : [])
            {
                var name = Path.GetRelativePath(packsDir, path);
                if (listed.Contains(name) || dirs.Count > 0 && MiddlewarePack.ReadHeader(path)?.Sources?.Any(Filled) != false) held.Add(name);   // no header or no sources: held
            }
            if (held.SetEquals(listed)) return held;
            if (held.Count == 0) File.Delete(listFile);   // what it listed is gone: a pack written there again is a new one
            else
            {
                var tmp = $"{listFile}.{Guid.NewGuid():N}.tmp";
                File.WriteAllLines(tmp, held.Order(StringComparer.OrdinalIgnoreCase));
                File.Move(tmp, listFile, true);
            }
        }
        return held;
    }

    /// <summary>Why a game's recording isn't shared from this build; null = it may be. <paramref name="engine"/> null (not read
    /// yet): not shared, the next pass looks again.</summary>
    public static string? UploadBlock(EngineInfo? engine, ForkContent content) =>
        engine is null ? "its engine isn't known yet" : ContentBlock(content);

    /// <summary>The part of <see cref="UploadBlock"/> about how the game's recorder runs now: alongside a mod, or under ReShade a
    /// copy can't reproduce.</summary>
    public static string? ContentBlock(ForkContent c) =>
        c.RecordedMinimal ? "its recording holds a launch recorded without NVAPI state that an earlier Kindling build held back: clear the recording to share it again"
        : c.RecordedLayered ? "its recording holds a launch alongside a mod's d3d12.dll or under a ReShade Kindling can't reproduce that an earlier Kindling build held back: clear the recording to share it again"
        : c.ChainedNow ? "its recorder runs alongside a mod's d3d12.dll: not shared while it does"
        : c.ReShade is { Copyable: false } ? "ReShade is loaded where Kindling can't reproduce it, so what it changes would pass for the game's own: not shared while it is"
        : null;
}

/// <summary>What <see cref="ForkBuild.ContentBlock"/> weighs for a game: its recorder now, chained to a mod's d3d12.dll (or
/// loaded by OptiScaler as its plugin), and ReShade where a copy can't reproduce it (an .asi, ReShade64.dll, a renamed file)
/// above the recorder, so what its add-ons change passes for the game's own.</summary>
public readonly record struct ForkContent(bool ChainedNow = false, ReShadeInstall? ReShade = null, bool RecordedMinimal = false, bool RecordedLayered = false);

/// <summary>fork.json: this build's own settings, which no official build reads or rewrites.</summary>
/// <param name="ShareRecordings">uploads from this unofficial build, besides Settings.ShareRecordings: off until the user ticks it</param>
public sealed record ForkSettings(bool ShareRecordings = false);
