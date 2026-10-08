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
    /// <summary>games\&lt;id&gt;\: a launch recorded alongside a mod's d3d12.dll, or under ReShade a copy can't reproduce, went
    /// into the recording since it was last cleared.</summary>
    public const string LayeredMarker = "recorded-layered.fork";
    /// <summary>The data folder's list of the upscaler packs (paths under the packs folder) a recording this build doesn't share
    /// filled: never shared from this build while the file is there, whatever is cleared since.</summary>
    public const string PacksHeld = "packs-held.fork";

    /// <summary>The engine families of the official build's readers: an index any other reader made (this fork's own) is never
    /// shared, nor one whose root signatures came from the game's files (<see cref="EngineInfo.ShipsRootSignatures"/>).</summary>
    public static readonly string[] OfficialFamilies = ["Unreal", Carved.CarvedReader.Family, Dagor.DagorReader.Family, FromSoft.FromSoftReader.Family,
        Northlight.NorthlightReader.Family, ReEngine.ReEngineReader.Family, RedEngine.RedEngineReader.Family, Unity.UnityReader.Family];

    /// <summary>Why a game's recording isn't shared from this build; null = it may be. <paramref name="engine"/> null (not read
    /// yet): not shared, the next pass looks again.</summary>
    public static string? UploadBlock(EngineInfo? engine, ForkContent content) =>
        engine is null ? "its engine isn't known yet"
        : !OfficialFamilies.Contains(engine.Family) || engine.ShipsRootSignatures ? $"its shaders were read by a reader only this unofficial build has ({engine.Family})"
        : ContentBlock(content);

    /// <summary>The part of <see cref="UploadBlock"/> about what the recording holds, whatever read the game: its markers, which
    /// only a clear takes away, then how its recorder runs now, which no clear changes.</summary>
    public static string? ContentBlock(ForkContent c) =>
        c.RecordedMinimal ? "a launch recorded without NVAPI state (pipelines only) is in its recording: clear the recording to share it again"
        : c.RecordedLayered ? "a launch recorded alongside a mod's d3d12.dll or under ReShade SCSKiller can't reproduce is in its recording: clear the recording to share it again"
        : c.NvapiOffNow ? "its recorder runs without NVAPI hooks (frame generation, or the crash guard stepped it down): not shared while it does"
        : c.ChainedNow ? "its recorder runs alongside a mod's d3d12.dll: not shared while it does"
        : c.ReShade is { Copyable: false } ? "ReShade is loaded where SCSKiller can't reproduce it, so what it changes would pass for the game's own: not shared while it is"
        : null;

    /// <summary>A scskiller.ini (or SCSKILLER_NVAPI) that turns the recorder's NVAPI hooks off: what it records has no 'N' state.</summary>
    public static bool NvapiOff(string? iniText, string? environment) =>
        environment?.Trim() == "0" || iniText != null && NvapiZero().IsMatch(iniText);

    /// <summary>The recorder's scskiller.log names a launch without its NVAPI hooks ("hooks off: ... NVAPI and Aftermath (...=0)").</summary>
    public static bool LoggedNvapiOff(string log) => LoggedOff().IsMatch(log);

    /// <summary>The recorder's scskiller.log names a launch chained to a mod's d3d12.dll ("next: ... (the device and every export ...").</summary>
    public static bool LoggedChained(string log) => log.Contains("(the device and every export it has come from it)", StringComparison.Ordinal);

    [GeneratedRegex(@"^[ \t]*nvapi[ \t]*=[ \t]*0[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex NvapiZero();

    [GeneratedRegex(@"hooks off: [^\r\n]*NVAPI and Aftermath \(")]
    private static partial Regex LoggedOff();
}

/// <summary>What <see cref="ForkBuild.ContentBlock"/> weighs for a game: its recording's markers (<see cref="ForkBuild.MinimalMarker"/>,
/// <see cref="ForkBuild.LayeredMarker"/>), and its recorder now: the ini or crash guard without NVAPI hooks, a mod's d3d12.dll
/// chained, ReShade where a copy can't reproduce it (an .asi, ReShade64.dll, a renamed file) above the recorder, so what its
/// add-ons change passes for the game's own.</summary>
public readonly record struct ForkContent(bool RecordedMinimal = false, bool RecordedLayered = false, bool NvapiOffNow = false, bool ChainedNow = false,
    ReShadeInstall? ReShade = null);

/// <summary>fork.json: this build's own settings, which no official build reads or rewrites.</summary>
/// <param name="ShareRecordings">uploads from this unofficial build, besides Settings.ShareRecordings: off until the user ticks it</param>
/// <param name="OldRecordingsMarked">the recordings made before this build kept its markers were looked at once (what their
/// recorder's log and record still tell), and marked</param>
public sealed record ForkSettings(bool ShareRecordings = false, bool OldRecordingsMarked = false);
