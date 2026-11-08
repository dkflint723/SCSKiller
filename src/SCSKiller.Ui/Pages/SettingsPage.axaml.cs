using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Unreal;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Pages;

public sealed partial class SettingsPage : UserControl
{
    public SettingsVm Vm { get; } = new();
    public AccountVm Patreon { get; } = new();
    readonly Coalesced refresh;

    public SettingsPage()
    {
        InitializeComponent();
        DataContext = Vm;
        // before the bindings subscribe: a new channel list (sign-in, sign-out, a lapse) moves the selection by itself,
        // synchronously or a little later, and none of that is a pick. Other refreshes keep the list and every pick.
        Patreon.PropertyChanged += (_, _) =>
        {
            if (refreshing || ReferenceEquals(Patreon.ChannelLabels, shownChannels)) return;
            shownChannels = Patreon.ChannelLabels;
            refreshing = true;
            Dispatcher.UIThread.Post(() => refreshing = false, DispatcherPriority.Background);
        };
        AccountPart.DataContext = Patreon;
        RemoveRecordersPart.IsVisible = !OperatingSystem.IsMacOS();   // no recorder there yet
        refresh = new(Host.Ui, Vm.GamesChanged);   // reconcile changes the recorder count
        Vm.PropertyChanged += (_, _) => Patreon.Refresh();   // e.g. the share checkbox ticked: the prompt goes
        // a new channel list (sign-in) resets the selection after the index was read: show the chosen channel again
        Patreon.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(() => ChannelBox.SetCurrentValue(SelectingItemsControl.SelectedIndexProperty, Patreon.ChannelIndex),
            DispatcherPriority.Background);
        Cards.SizeChanged += (_, e) => { if (e.WidthChanged) FitCards(); };
        PropertyChanged += (_, e) => { if (e.Property == FontSizeProperty) FitCards(); };
    }

    // two cards side by side need about 26 lines of text width each, so a large Text size stacks them
    void FitCards()
    {
        bool narrow = (Cards.Bounds.Width - Cards.ColumnSpacing) / 2 < FontSize * 26;
        if (narrow == Scroller.Classes.Contains("Narrow")) return;
        Scroller.Classes.Set("Narrow", narrow);
        Cards.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,*");
        Cards.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto,Auto,Auto" : "Auto,Auto");
        for (int i = 0; i < Cards.Children.Count; i++)
        {
            Grid.SetColumn(Cards.Children[i], narrow ? 0 : i % 2);
            Grid.SetRow(Cards.Children[i], narrow ? i : i / 2);
        }
    }

    void OnChanged(GameState _) => refresh.Request();

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Patreon.Watch(true);
        Vm.Watch(true);
        Host.Core.GameChanged += OnChanged;
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        Patreon.Watch(false);
        Vm.Watch(false);
        Host.Core.GameChanged -= OnChanged;
    }

    void OnSignIn(object? _, RoutedEventArgs __) => AccountVm.SignIn();
    void OnCancelSignIn(object? _, RoutedEventArgs __) => AccountVm.CancelSignIn();
    void OnRefreshAccount(object? _, RoutedEventArgs __) => AccountVm.RefreshStatus();
    void OnSignOut(object? _, RoutedEventArgs __) => AccountVm.SignOut();
    void OnBackToStable(object? _, RoutedEventArgs __) => AccountVm.BackToStable();

    bool refreshing;
    object? shownChannels;
    void OnChannelPicked(object? _, SelectionChangedEventArgs __)
    {
        if (!refreshing) Patreon.ChannelIndex = ChannelBox.SelectedIndex;
    }

    /// <summary>--screenshots: a pick as UI Automation makes it, without focus or the drop-down.</summary>
    public void PickChannel(int index) => ChannelBox.SelectedIndex = index;

    void OnShare(object? _, RoutedEventArgs __) => Answer(share: true);
    void OnNotNow(object? _, RoutedEventArgs __) => Answer(share: false);
    void Answer(bool share)
    {
        AccountVm.AnswerSharing(share);
        Vm.Refresh();   // the share checkbox; its PropertyChanged refreshes the prompt too
    }

    static Task<bool> Confirm(string title, string text, string yes) => App.ConfirmAsync(title, text, yes);
    async void OnApply(object? _, RoutedEventArgs __) => await Vm.ApplyLimitAsync(Confirm);
    async void OnApplyAuto(object? _, RoutedEventArgs __) => await Vm.ApplyAutoAsync(Confirm);
    async void OnShaderCleanup(object? _, RoutedEventArgs __) => await Vm.ApplyShaderCleanupAsync();

    // Stands in for the website's recorder-anti-cheat page until it exists.
    public static ContentDialog AntiCheatDetailsDialog() => new()
    {
        Title = "Recording in games with anti-cheat", CloseButtonText = "Close",
        Content = "For one session, Kindling puts its recorder (a d3d12.dll) next to the game's exe.\n\n"
            + "Kindling removes every file it places when the game exits, or after a crash at your next logon.\n\n"
            + "Enabling the recorder in games with anti-cheat is at your own risk. Kindling isn't responsible for bans.",
    };

    async void OnAntiCheatDetails(object? _, RoutedEventArgs __) => await App.ShowAsync(AntiCheatDetailsDialog());

    async void OnRemoveRecorders(object? _, RoutedEventArgs __)
    {
        if (Host.Core is not ScsKiller real || !await App.ConfirmAsync("Remove recorders?",
                "Kindling removes its recorder from every game, then quits. Close your games first: a game that's running keeps its recorder. Recording resumes when Kindling starts again.", "Remove and quit")) return;
        var removed = await Task.Run(real.RemoveRecorders);
        string[] left = [.. removed.Games.Where(r => r.Left != null).Select(r => $"{real.Games.FirstOrDefault(s => s.Game.Id == r.GameId)?.Game.Name ?? r.Folder}: {r.Left}"),
            .. removed.VulkanLeft is { } vulkan ? [$"Vulkan recorders' registrations: {vulkan}"] : Array.Empty<string>()];
        if (left.Length == 0)
        {
            // a test identity's toast registration goes at quit
            ScsKiller.RemoveTraces(Program.Identity.Production ? Environment.ProcessPath : null, m => real.Log?.Report(m));
            App.Quit();
            return;
        }
        // SCSKiller stays open, so the user can remove them again once a game is closed
        await App.ShowAsync(new ContentDialog
        {
            Title = "Some recorders are still in place",
            Content = new TextBlock { Text = string.Join("\n", left) + "\n\nClose any game named here, then select Remove recorders again.", TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
        });
    }

    /// <summary>Also --screenshots: the games whose AMD cache is kept aside, each with its size and a Remove.</summary>
    public ContentDialog ManageKeptDialog()
    {
        var rows = new StackPanel { Spacing = 10 };
        foreach (var g in SettingsVm.KeptGames)
        {
            var row = new Grid { ColumnSpacing = 12, ColumnDefinitions = new("*,Auto") };
            row.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = g.Game.Name, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = Fmt.KeptSize(g.Kept!), Classes = { "Secondary" } },
                },
            });
            var remove = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Center };
            remove.Click += (_, _) => { Host.Core.DropKeptCache(g.Game.Id); rows.Children.Remove(row); Vm.Refresh(); };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            rows.Children.Add(row);
        }
        return new()
        {
            Title = "Kept aside", CloseButtonText = "Close",
            Content = new StackPanel
            {
                Spacing = 14, MinWidth = 420,
                Children =
                {
                    new TextBlock { Text = "Removing frees the space. The game is kept again after its next compile.", TextWrapping = TextWrapping.Wrap },
                    new ScrollViewer { MaxHeight = 360, Content = rows },
                },
            },
        };
    }

    async void OnManageKept(object? _, RoutedEventArgs __) => await App.ShowAsync(ManageKeptDialog());

    // Import keys…: a file of keys the user collected, tried on every encrypted Unreal game (nothing is sent); the results
    // per game in a dialog, each outcome in words.
    async void OnImportKeys(object? _, RoutedEventArgs __)
    {
        string? path;
        try
        {
            var picked = await App.Main.StorageProvider.OpenFilePickerAsync(new()
            {
                Title = "Import keys",
                FileTypeFilter = [new("Key files") { Patterns = ["*.txt", "*.csv", "*.json", "*.htm", "*.html"] }, new("All files") { Patterns = ["*"] }],
            });
            path = picked.FirstOrDefault()?.TryGetLocalPath();
        }
        catch (Exception ex) { (ImportKeysStatus.Text, ImportKeysStatus.IsVisible) = ($"Couldn't open the file picker: {ex.Message}", true); return; }
        if (path == null) return;
        (ImportKeysButton.IsEnabled, ImportKeysRing.IsActive, ImportKeysRing.IsVisible, ImportKeysStatus.IsVisible) = (false, true, true, true);
        ImportKeysStatus.Text = "Reading the file…";
        var done = false;   // a stage reported late doesn't cover the result
        KeyImport r;
        try { r = await Host.Core.ImportKeysAsync(path, new Progress<string>(m => { if (!done) ImportKeysStatus.Text = m; })); }
        catch (Exception ex) { r = new(0, 0, [], ex.Message.TrimEnd('.')); }
        done = true;
        (ImportKeysButton.IsEnabled, ImportKeysRing.IsActive, ImportKeysRing.IsVisible) = (true, false, false);
        var summary = SettingsVm.ImportSummary(r);
        ImportKeysStatus.Text = summary;
        if (r.Problem != null) return;
        var rescan = r.Unlocked > 0 ? Host.Core.RescanAsync(CancellationToken.None) : null;
        if (IsLoaded) await App.ShowAsync(ImportResultsDialog(r, rescan != null));   // the user left Settings meanwhile: no dialog from a page that's gone
        try { if (rescan != null) await rescan; }
        catch (Exception ex) { ImportKeysStatus.Text = $"{summary} Checking the games again failed: {ex.Message}"; }
    }

    /// <summary>Also --screenshots: Import keys…'s results, a line per Unreal game.</summary>
    public static ContentDialog ImportResultsDialog(KeyImport r, bool rescanning)
    {
        var list = new StackPanel { Spacing = 8 };
        list.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = SettingsVm.ImportSummary(r) + (rescanning ? " The unlocked games are checked again now." : r.Games.Count == 0 ? " There are no Unreal games in your library." : ""),
        });
        list.Children.AddRange(r.Games.Select(g => new TextBlock { Text = SettingsVm.ImportLine(g), TextWrapping = TextWrapping.Wrap }));
        return new ContentDialog
        {
            Title = r.Unlocked > 0 ? "Keys imported" : "No game unlocked", CloseButtonText = "OK",
            Content = new ScrollViewer { Content = list, MaxHeight = 420 },
        };
    }

    public void ScrollToEnd() => Scroller.ScrollToEnd();
    /// <summary>The Patreon card at the top.</summary>
    public void ScrollToPatreon() => ScrollTo(PatreonCard, -16);
    /// <summary>--screenshots: how far the page is scrolled.</summary>
    public double ScrolledBy => Scroller.Offset.Y;
    public void ScrollToNewShaders() => ScrollTo(NewShadersHeader, 40);
    public void ScrollToTraversal() => ScrollTo(TraversalLabel, 300);
    public void ScrollToCompactUi() => ScrollTo(CompactUiBox, 300);
    // WinUI's BringIntoView at the top with a VerticalOffset: the element <offset> px below the viewport's top (above it when negative)
    void ScrollTo(Control element, double offset)
    {
        if (element.TranslatePoint(new(0, 0), (Control)Scroller.Content!) is { } at)
            Scroller.Offset = new(0, Math.Clamp(at.Y - offset, 0, Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height)));
    }
}
