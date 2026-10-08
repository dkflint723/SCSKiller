using System.Text.RegularExpressions;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.App;

/// <summary>This build is an unofficial fork of SCSKiller (dkflint723's), and says so to the official servers it talks to:
/// its own User-Agent product and comment, never one of the official channels; its own anonymous upload device
/// (<see cref="UploadDevice"/>, never an official build's upload.dat); its own opt-in to sharing (<see cref="SettingsFile"/>),
/// apart from settings.json, which official builds share and rewrite; and a share only of recordings an official build
/// could have made (<see cref="UploadBlock"/>). The version is 0.0.0-dkfork.N (Directory.Build.props): the internal channel,
/// so no update check or active check either.</summary>
public static partial class ForkBuild
{
    public const string Product = "SCSKiller-fork-dkflint723";
    /// <summary>The User-Agent comment in place of the channel.</summary>
    public const string Comment = "unofficial";
    public const string UploadDevice = "upload-fork.dat";
    public const string SettingsFile = "fork.json";
    /// <summary>games\&lt;id&gt;\: a launch recorded without its NVAPI state (<see cref="RecorderLevel.Minimal"/>) went into the
    /// recording since it was last cleared.</summary>
    public const string MinimalMarker = "recorded-minimal.fork";

    /// <summary>The engine families of the official build's readers: an index any other reader made (this fork's own) is never
    /// shared, nor one whose root signatures came from the game's files (<see cref="EngineInfo.ShipsRootSignatures"/>).</summary>
    public static readonly string[] OfficialFamilies = ["Unreal", Carved.CarvedReader.Family, Dagor.DagorReader.Family, FromSoft.FromSoftReader.Family,
        Northlight.NorthlightReader.Family, ReEngine.ReEngineReader.Family, RedEngine.RedEngineReader.Family, Unity.UnityReader.Family];

    /// <summary>Why a game's recording isn't shared from this build; null = it may be. <paramref name="engine"/> null (not read
    /// yet): not shared, the next pass looks again. <paramref name="reshade"/>: ReShade where a copy can't reproduce it (an
    /// .asi, ReShade64.dll, a renamed file) sits above the recorder, so what its add-ons change passes for the game's own.</summary>
    public static string? UploadBlock(EngineInfo? engine, bool recordedMinimal, bool alongsideMod, ReShadeInstall? reshade) =>
        engine is null ? "its engine isn't known yet"
        : !OfficialFamilies.Contains(engine.Family) || engine.ShipsRootSignatures ? $"its shaders were read by a reader only this unofficial build has ({engine.Family})"
        : ContentBlock(recordedMinimal, alongsideMod, reshade);

    /// <summary>The part of <see cref="UploadBlock"/> about what the recording holds, whatever read the game (an upscaler
    /// pack is filled from every game's recording).</summary>
    public static string? ContentBlock(bool recordedMinimal, bool alongsideMod, ReShadeInstall? reshade) =>
        recordedMinimal ? "a launch recorded without NVAPI state (pipelines only) is in its recording: clear the recording to share it again"
        : alongsideMod ? "it is recorded alongside a mod's d3d12.dll"
        : reshade is { Copyable: false } ? "ReShade is loaded where SCSKiller can't reproduce it, so what it changes would pass for the game's own"
        : null;

    /// <summary>A scskiller.ini (or SCSKILLER_NVAPI) that turns the recorder's NVAPI hooks off: what it records has no 'N' state.</summary>
    public static bool NvapiOff(string? iniText, string? environment) =>
        environment?.Trim() == "0" || iniText != null && NvapiZero().IsMatch(iniText);

    [GeneratedRegex(@"^[ \t]*nvapi[ \t]*=[ \t]*0[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex NvapiZero();
}

/// <summary>fork.json: this build's own settings, which no official build reads or rewrites.</summary>
/// <param name="ShareRecordings">uploads from this unofficial build, besides Settings.ShareRecordings: off until the user ticks it</param>
public sealed record ForkSettings(bool ShareRecordings = false);
