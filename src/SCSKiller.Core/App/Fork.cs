using SCSKiller.Core.Games;

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

    /// <summary>Why a game's recording isn't shared from this build; null = it may be. <paramref name="engine"/> null (not read
    /// yet): not shared, the next pass looks again.</summary>
    public static string? UploadBlock(EngineInfo? engine, ForkContent content) =>
        engine is null ? "its engine isn't known yet" : ContentBlock(content);

    /// <summary>The part of <see cref="UploadBlock"/> about how the game's recorder runs now: alongside a mod, or under ReShade a
    /// copy can't reproduce.</summary>
    public static string? ContentBlock(ForkContent c) =>
        c.ChainedNow ? "its recorder runs alongside a mod's d3d12.dll: not shared while it does"
        : c.ReShade is { Copyable: false } ? "ReShade is loaded where Kindling can't reproduce it, so what it changes would pass for the game's own: not shared while it is"
        : null;
}

/// <summary>What <see cref="ForkBuild.ContentBlock"/> weighs for a game: its recorder now, chained to a mod's d3d12.dll (or
/// loaded by OptiScaler as its plugin), and ReShade where a copy can't reproduce it (an .asi, ReShade64.dll, a renamed file)
/// above the recorder, so what its add-ons change passes for the game's own.</summary>
public readonly record struct ForkContent(bool ChainedNow = false, ReShadeInstall? ReShade = null);

/// <summary>fork.json: this build's own settings, which no official build reads or rewrites.</summary>
/// <param name="ShareRecordings">uploads from this unofficial build, besides Settings.ShareRecordings: off until the user ticks it</param>
public sealed record ForkSettings(bool ShareRecordings = false);
