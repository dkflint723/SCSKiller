using SCSKiller.Core.App;

namespace SCSKiller.Ui.Models;

/// <summary>What a start that failed says, as the WinUI app's message box does: where the crash log is. When the log
/// couldn't be written either (a data folder that can't be written), the error itself.</summary>
public sealed record StartFailure(string Text, string? LogPath)
{
    public const string Title = ForkBuild.Name;

    public static StartFailure Of(Exception e, string dataDir, bool logged)
    {
        var log = Path.Combine(dataDir, CrashLog.FileName);
        return logged ? new($"{ForkBuild.Name} couldn't start. Details are in {log}", log) : new($"{ForkBuild.Name} couldn't start. {e.Message}", null);
    }
}
