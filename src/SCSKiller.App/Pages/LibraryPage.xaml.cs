using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using SCSKiller.Core;
using SCSKiller.Core.Unreal;

namespace SCSKiller.App.Pages;

public sealed partial class LibraryPage : Page
{
    public LibraryVm Vm { get; } = new();

    public LibraryPage()
    {
        InitializeComponent();
        ((CollectionViewSource)Resources["GroupedGames"]).Source = Vm.Groups;
        var refresh = new Coalesced(DispatcherQueue, Vm.Refresh);
        void OnChanged(GameState _) => refresh.Request();
        void OnQueue(QueueItem q) { if (q.Stage != QueueStage.Warming) refresh.Request(); }   // progress ticks change nothing here
        void OnStep(string? step) => DispatcherQueue.TryEnqueue(() => Vm.ScanStep = step);   // which game the scan reads
        Loaded += (_, _) =>
        {
            App.Core.GameChanged += OnChanged; App.Core.QueueChanged += OnQueue; Icons.Failed += refresh.Request;
            if (App.Core is Core.App.ScsKiller k) k.ScanProgress += OnStep;
            Vm.Load();
        };
        Unloaded += (_, _) =>
        {
            App.Core.GameChanged -= OnChanged; App.Core.QueueChanged -= OnQueue; Icons.Failed -= refresh.Request;
            if (App.Core is Core.App.ScsKiller k) k.ScanProgress -= OnStep;
        };
    }

    void OnSearch(AutoSuggestBox box, AutoSuggestBoxTextChangedEventArgs _) => Vm.Filter = box.Text;

    void OnSearchKey(object _, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape || Search.Text.Length == 0) return;
        Search.Text = "";
        e.Handled = true;
    }

    /// <summary>--screenshots: a search typed, and the list scrolled to its last section.</summary>
    public string SearchText { set => Search.Text = value; }
    public void ScrollToEnd() { if (Vm.Groups.Count > 0) List.ScrollIntoView(Vm.Groups[^1][^1]); }

    static GameRow RowOf(object sender) => (GameRow)((FrameworkElement)sender).DataContext;

    void OnGameClick(object _, ItemClickEventArgs e) => App.Main.Navigate(typeof(DetailPage), ((GameRow)e.ClickedItem).Id);
    void OnDetails(object sender, RoutedEventArgs _) => App.Main.Navigate(typeof(DetailPage), RowOf(sender).Id);
    void OnSettings(object _, RoutedEventArgs __) => App.Main.Navigate(typeof(SettingsPage));
    void OnShowUnsupported(object _, RoutedEventArgs __) => Vm.HideUnsupported = false;
    void OnPlay(object sender, RoutedEventArgs _) => Vm.Play(RowOf(sender));

    // Either click fetches the server's data whatever its age; Shift+click also forces re-detection, which is otherwise
    // redone only when an exe, build or SCSKiller changes.
    void OnRefresh(object _, RoutedEventArgs __) =>
        Vm.Rescan(force: Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down), userRequested: true);

    // Adding only queues the game: nothing compiles until Compile queue.
    void OnAdd(object sender, RoutedEventArgs _)
    {
        App.Core.Enqueue(RowOf(sender).Id);
        Vm.Refresh();
    }

    void OnDismissGpuNotice(InfoBar _, object __) => Vm.DismissGpuNotice();
    // A launcher stub is resolved to the game's own exe, shown with the game folder to confirm; an exe of a listed game
    // opens that game instead.
    async void OnAddGame(object _, RoutedEventArgs __)
    {
        string? path;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.Main.AppWindow.Id)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder, CommitButtonText = "Add game",
            };
            picker.FileTypeFilter.Add(".exe");
            path = (await picker.PickSingleFileAsync())?.Path;
        }
        catch (Exception ex) { await Message("Couldn't open the file picker", ex.Message); return; }
        if (path == null) return;
        ManualAdd added;
        try { added = await Task.Run(() => App.Core.PreviewManualGame(path)); }   // reads the install's files
        catch (Exception ex)   // a rejection's message, or a data folder SCSKiller couldn't read
        {
            await Message("Couldn't add this game", ex.Message);
            return;
        }
        if (!added.Existed)
        {
            if (await GameFolderDialog.ShowAsync(XamlRoot, added.Game, $"Add {added.Game.Name}", "Add game") is not { } folder) return;
            try { await Task.Run(() => App.Core.AddManualGame(path, folder)); }
            catch (Exception ex)
            {
                await Message("Couldn't add this game", ex.Message);
                return;
            }
            Vm.Rescan(force: false);   // lists it under "Added by you" once its engine and anti-cheat are checked
            return;
        }
        var listed = App.Core.Games.Any(s => s.Game.Id == added.Game.Id);
        var where = added.Game.Store == Store.Manual ? "you added it already" : $"SCSKiller found it in {Fmt.StoreName(added.Game)}";
        if (listed && await App.ConfirmAsync(this, "Already in your library", $"{added.Game.Name} is in the list: {where}.", "Open", ContentDialogButton.Primary))
            App.Main.Navigate(typeof(DetailPage), added.Game.Id);
        else if (!listed) await Message("Already in your library", $"{added.Game.Name}: {where}. Refresh the library to see it.");
    }

    Task Message(string title, string text) => App.ShowAsync(new ContentDialog
    {
        XamlRoot = XamlRoot, Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "OK",
    });

    void OnAddAll(object _, RoutedEventArgs __) => Vm.AddAllReady();
    void OnAddRecommended(object _, RoutedEventArgs __) => Vm.AddAllRecommended();

    void OnCompileQueue(object _, RoutedEventArgs __)
    {
        App.CompileQueue();
        App.Main.Navigate(typeof(QueuePage));
    }

    // Unsupported: say why; an encrypted game can be unlocked with its AES key (stored on this PC only).
    async void OnWhy(object sender, RoutedEventArgs _)
    {
        var row = RowOf(sender);
        var text = new TextBlock { Text = row.Reason, TextWrapping = TextWrapping.Wrap };
        var box = new TextBox { PlaceholderText = "0x followed by 64 hex digits", Header = "AES key", Visibility = row.IsEncrypted ? Visibility.Visible : Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 12, Children = { text, box } };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"Why can't {row.Name} be compiled?", Content = panel, CloseButtonText = "Close",
            PrimaryButtonText = row.IsEncrypted ? "Unlock" : "", DefaultButton = ContentDialogButton.Close,
        };
        if (row.IsEncrypted)
        {
            string? problem;
            try { problem = await Task.Run(() => App.Core.KeyProblem(row.Id)); }   // reads what the key search recorded
            catch (Exception) { problem = null; }
            panel.Children.Insert(1, new TextBlock { TextWrapping = TextWrapping.Wrap, Text =
                (problem ?? "SCSKiller couldn't find this game's key in its exe (protected exes hide it). Look the key up online, or paste it if you have it.") +
                " A key is checked against the game's files and kept on this PC only." });
            AddKeyLookup(row, panel, box, dialog);
        }
        if (await App.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        bool ok;
        string why = "It doesn't open this game's files.";
        var key = box.Text.Trim();
        try { ok = await Task.Run(() => App.Core.SetEncryptionKey(row.Id, key)); }   // opens the game's files
        catch (Exception ex) { ok = false; why = $"Checking it failed: {ex.Message}"; }   // the first dialog is closed: say it in this one
        if (ok) Vm.Rescan();
        else await App.ShowAsync(new ContentDialog { XamlRoot = XamlRoot, Title = "That key didn't work", Content = why, CloseButtonText = "OK" });
    }

    // The Why? dialog's "Look up key online": the community's key list (the key itself is never shown). The forum may ask
    // for a browser check; the page saved from a browser is then read instead (and cached for every game).
    void AddKeyLookup(GameRow row, StackPanel panel, TextBox box, ContentDialog dialog)
    {
        var lookup = new Button { Content = "Look up key online" };
        var open = new Button { Content = "Open list in browser" };
        var saved = new Button { Content = "Load saved page…" };
        var ring = new ProgressRing { IsActive = false, Width = 20, Height = 20, Visibility = Visibility.Collapsed };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { lookup, ring } });
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { open, saved } });
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Caption"],
            Text = "Open the list, save the page (Ctrl+S, 'Webpage, HTML only' is enough), then load it here." });
        panel.Children.Add(status);
        async Task LookUp(string? page)
        {
            (lookup.IsEnabled, saved.IsEnabled, ring.IsActive, ring.Visibility, status.Visibility) = (false, false, true, Visibility.Visible, Visibility.Visible);
            status.Text = page == null ? "Looking up the key…" : "Checking the saved page…";
            var done = false;   // a stage reported late doesn't cover the result
            KeyLookup r;
            try { r = await App.Core.LookUpKeyAsync(row.Id, page, new Progress<string>(m => { if (!done) status.Text = m; })); }
            catch (Exception ex) { r = new(KeyLookupOutcome.FetchFailed, $"The lookup failed: {ex.Message}"); }
            done = true;
            var unlocked = r.Outcome == KeyLookupOutcome.Unlocked;
            (ring.IsActive, ring.Visibility, lookup.IsEnabled, saved.IsEnabled) = (false, Visibility.Collapsed, !unlocked, !unlocked);
            status.Text = unlocked ? r.Message + " The game is checked again now." : r.Message;
            if (!unlocked) return;
            (box.IsEnabled, dialog.IsPrimaryButtonEnabled) = (false, false);
            Vm.Rescan();
        }
        lookup.Click += async (_, _) => await LookUp(null);
        open.Click += (_, _) =>
        {
            var url = App.Core.Settings.KeyListUrl is { Length: > 0 } u ? u.Trim() : KeyCollection.DefaultUrl;
            string? problem = null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) problem = "The key list's address in Settings isn't an https:// address.";
            else
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
                catch (System.ComponentModel.Win32Exception e) { problem = $"Couldn't open the browser: {e.Message}"; }   // no browser for https
            if (problem != null) (status.Text, status.Visibility) = (problem, Visibility.Visible);
        };
        saved.Click += async (_, _) =>
        {
            string? page = null;
            try
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.Main.AppWindow.Id) { CommitButtonText = "Load" };
                picker.FileTypeFilter.Add(".html");
                picker.FileTypeFilter.Add(".htm");
                picker.FileTypeFilter.Add("*");
                if ((await picker.PickSingleFileAsync())?.Path is { } path)
                {
                    if (KeyCollection.TooLarge(new FileInfo(path).Length) is { } large)   // said as that, not as a page without keys
                    {
                        (status.Text, status.Visibility) = ($"Couldn't read that page: {large} ('Webpage, HTML only' is enough).", Visibility.Visible);
                        return;
                    }
                    page = await Task.Run(() => File.ReadAllText(path));
                }
            }
            catch (Exception ex) { (status.Text, status.Visibility) = ($"Couldn't read that page: {ex.Message}", Visibility.Visible); return; }
            if (page != null) await LookUp(page);
        };
    }

    async void OnRecord(object sender, RoutedEventArgs _)
    {
        var row = RowOf(sender);
        if (!await App.ConfirmAsync(this, $"Record {row.Name}?",
                "SCSKiller adds a small d3d12.dll next to the game that writes down every pipeline it creates. " +
                "Play for about 5 minutes, close the game, then add it to the compile queue. You can remove it any time from the game's page.",
                "Add recorder", ContentDialogButton.Primary)) return;
        try { await Task.Run(() => App.Core.InstallRecorder(row.Id)); }   // file IO and the game's re-evaluation
        catch (Exception ex) { await App.ShowAsync(new ContentDialog { XamlRoot = XamlRoot, Title = "Couldn't add the recorder", Content = ex.Message, CloseButtonText = "OK" }); }
        Vm.Refresh();
    }
}
