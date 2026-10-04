using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SCSKiller.Core;
using System.Globalization;
using SCSKiller.Core.App;
using SCSKiller.Core.Unreal;

namespace SCSKiller.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsVm Vm { get; } = new();
    public AccountVm Patreon { get; } = new();

    public SettingsPage()
    {
        InitializeComponent();
        var refresh = new Coalesced(DispatcherQueue, Vm.GamesChanged);   // reconcile changes the recorder count
        void OnChanged(GameState _) => refresh.Request();
        Loaded += (_, _) => { Patreon.Watch(true); App.Core.GameChanged += OnChanged; };
        Unloaded += (_, _) => { Patreon.Watch(false); App.Core.GameChanged -= OnChanged; };
        Vm.PropertyChanged += (_, _) => Patreon.Refresh();   // e.g. the share checkbox ticked: the prompt goes
    }

    // Account: failures show in the card (Account.Problem), so these never throw.
    void OnSignIn(object _, RoutedEventArgs __) => _ = App.Account.SignInAsync();
    void OnCancelSignIn(object _, RoutedEventArgs __) => App.Account.CancelSignIn();
    void OnRefreshAccount(object _, RoutedEventArgs __) => _ = App.Account.RefreshAsync();
    void OnSignOut(object _, RoutedEventArgs __) { if (!Updater.Restarting) _ = App.Account.SignOutAsync(); }

    // The latest stable, even though it's older than this pre-release (a downgrade, allowed this once).
    void OnBackToStable(object _, RoutedEventArgs __)
    {
        if (Updater.Restarting) return;   // the restart applies the update it waited for, or none
        App.Core.Settings = App.Core.Settings with { UpdateChannel = UpdateChannels.Stable };
        _ = Updater.CheckAsync(backToStable: true);
    }

    // The post-sign-in share prompt (Account.OffersSharing).
    void OnShare(object _, RoutedEventArgs __) => Answer(share: true);
    void OnNotNow(object _, RoutedEventArgs __) => Answer(share: false);
    void Answer(bool share)   // asked once, ever: either answer dismisses it
    {
        App.Core.Settings = App.Core.Settings with { SharePromptDismissed = true, ShareRecordings = share || App.Core.Settings.ShareRecordings };
        Vm.Refresh();   // the share checkbox; its PropertyChanged refreshes the prompt too
    }

    void OnResetKeyList(object _, RoutedEventArgs __) => Vm.KeyListUrl = KeyCollection.DefaultUrl;

    // A file of keys the user collected, tried on every encrypted Unreal game; the outcomes in a dialog (never a key). After
    // an unlock the games are checked again, as after a pasted key.
    async void OnImportKeys(object _, RoutedEventArgs __)
    {
        string? path;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.Main.AppWindow.Id) { CommitButtonText = "Import" };
            foreach (var type in new[] { ".txt", ".csv", ".json", ".htm", ".html", "*" }) picker.FileTypeFilter.Add(type);
            path = (await picker.PickSingleFileAsync())?.Path;
        }
        catch (Exception ex) { (ImportKeysStatus.Text, ImportKeysStatus.Visibility) = ($"Couldn't pick a file: {ex.Message}", Visibility.Visible); return; }
        if (path == null) return;
        (ImportKeysButton.IsEnabled, ImportKeysRing.IsActive, ImportKeysRing.Visibility, ImportKeysStatus.Visibility) = (false, true, Visibility.Visible, Visibility.Visible);
        ImportKeysStatus.Text = "Reading the file…";
        var done = false;
        KeyImport r;
        try { r = await App.Core.ImportKeysAsync(path, new Progress<string>(m => { if (!done) ImportKeysStatus.Text = m; })); }
        catch (Exception ex) { r = new(0, 0, [], ex.Message.TrimEnd('.')); }
        done = true;
        (ImportKeysButton.IsEnabled, ImportKeysRing.IsActive, ImportKeysRing.Visibility) = (true, false, Visibility.Collapsed);
        if (r.Problem != null) { ImportKeysStatus.Text = $"Nothing imported: {r.Problem}."; return; }
        var summary = $"{r.Named:N0} named and {r.Unnamed:N0} unnamed keys read; {r.Unlocked} of {r.Games.Count(g => g.Outcome != KeyImportOutcome.Skipped)} encrypted games unlocked.";
        ImportKeysStatus.Text = summary;
        var rescan = r.Unlocked > 0 ? App.Core.RescanAsync(CancellationToken.None) : null;
        var list = new StackPanel { Spacing = 8, Children = { new TextBlock { TextWrapping = TextWrapping.Wrap,
            Text = summary + (rescan != null ? " The unlocked games are checked again now." : r.Games.Count == 0 ? " There are no Unreal games in your library." : "") } } };
        foreach (var g in r.Games) list.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"{g.Name}: {g.Message}" });
        await new ContentDialog
        {
            XamlRoot = XamlRoot, Title = r.Unlocked > 0 ? "Keys imported" : "No game unlocked", CloseButtonText = "OK",
            Content = new ScrollViewer { Content = list, MaxHeight = 420 },
        }.ShowAsync();
        try { if (rescan != null) await rescan; }
        catch (Exception ex) { ImportKeysStatus.Text = $"{summary} Checking the games again failed: {ex.Message}"; }
    }

    public void ScrollToEnd() => Scroller.ChangeView(null, Scroller.ScrollableHeight, null, true);
    /// <summary>--screenshots: the Patreon card at the top.</summary>
    public void ScrollToPatreon() => PatreonCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, VerticalOffset = -16, AnimationDesired = false });

    // SetCacheLimit is a global driver setting that needs admin: only after the user's explicit OK.
    async void OnApply(object _, RoutedEventArgs __)
    {
        var (label, limit) = SettingsVm.Sizes[SizeBox.SelectedIndex];
        if (!await App.ConfirmAsync(this, $"Set the shader cache limit to {label}?",
                $"This is a global {Fmt.Vendor(App.Core.Vendor.Vendor)} driver setting: it applies to every game on this PC, not only the ones SCSKiller compiles, " +
                "and stays until you change it. Windows will ask for administrator permission.\n\n" +
                "Raising it deletes nothing. Lowering it below what is used now makes the driver delete older entries, and those games stutter again. " +
                "You can switch back to the driver default here at any time.",
                "Change limit")) return;

        ApplyButton.IsEnabled = false;
        var size = limit.IsDriverDefault ? "default" : limit.Bytes is { } b ? (b / (double)(1L << 30)).ToString(CultureInfo.InvariantCulture) : "unlimited";
        (Vm.ApplyMessage, Vm.ApplySeverity) = await AsAdminAsync(() => App.Core.Vendor.SetCacheLimit(limit), ["cache", "set", size]) switch
        {
            null => ("Cancelled.", InfoBarSeverity.Informational),
            { Ok: true } => ($"Shader cache limit set to {label}.", InfoBarSeverity.Success),
            var r => ("The limit was not changed: " + r.Message, InfoBarSeverity.Error),
        };
        ApplyButton.IsEnabled = true;
        Vm.Refresh();
    }

    // Admin-only actions: in this process when it is already elevated (or shows the fake), else the bundled CLI behind a
    // UAC prompt (Elevated.Run). Null = the user cancelled the prompt; an exception is a failure like the CLI's.
    static Task<Elevated.Result?> AsAdminAsync(Action inProcess, string[] cliArgs) => Task.Run(() =>
    {
        try
        {
            if (App.Core is not Design.FakeScsKiller && !Elevated.IsAdmin) return Elevated.Run(cliArgs);
            inProcess();
            return new Elevated.Result(true, "");
        }
        catch (Exception ex) { return new Elevated.Result(false, ex.Message); }
    });

    // Auto Shader Compilation: a global driver setting + the driver's idle task, admin: only after the user's explicit OK.
    async void OnApplyAuto(object _, RoutedEventArgs __)
    {
        var level = Vm.AutoChoice;
        if (!await App.ConfirmAsync(this, level == AutoShaderCompilation.Off ? "Turn off NVIDIA Auto Shader Compilation?" : $"Turn on NVIDIA Auto Shader Compilation ({level})?",
                "Auto Shader Compilation is a beta feature by NVIDIA®.", level == AutoShaderCompilation.Off ? "Turn off" : "Turn on")) return;

        AutoApplyButton.IsEnabled = false;
        // --for-user: the elevated CLI refuses when UAC elevated another administrator (NvOSC would register the task for them).
        string[] cli = ["nvidia-auto-shader", level.ToString().ToLowerInvariant(), Elevated.ForUserArg, Elevated.CurrentUserSid];
        (Vm.AutoMessage, Vm.AutoSeverity) = await AsAdminAsync(() => App.Core.Vendor.SetAutoShaderCompilation(level), cli) switch
        {
            null => ("Cancelled.", InfoBarSeverity.Informational),
            { Ok: true } => (level == AutoShaderCompilation.Off ? "Turned off." : $"Turned on ({level}).", InfoBarSeverity.Success),
            var r => ("Not changed: " + r.Message, InfoBarSeverity.Error),
        };
        AutoApplyButton.IsEnabled = true;
        Vm.Refresh();   // re-reads the setting and the task
    }
}
