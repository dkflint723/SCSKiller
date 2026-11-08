using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SCSKiller.Core.App;
using SCSKiller.Core.Unreal;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Pages;

/// <summary>The user's own AES keys for an encrypted Unreal game, or a BlackSpace game's key: checked against the game's
/// files and stored on this PC only.</summary>
static class AesKeyDialog
{
    /// <summary>True once a key that opens the game's files is stored; the caller then re-reads the game.
    /// <paramref name="reason"/>: the Library's Why?, shown above the key. <paramref name="lookedUp"/>: the Why? dialog's key
    /// lookup (<see cref="AddKeyLookup"/>) is offered, and runs this once it stores a key, with the dialog still open.</summary>
    public static async Task<bool> ShowAsync(string id, string title, string? reason = null, bool aes = true, Action? lookedUp = null)
    {
        var (dialog, box) = Dialog(title, reason, aes, aes && lookedUp != null ? id : null, lookedUp);
        if (await App.ShowAsync(dialog) != ContentDialogResult.Primary) return false;
        var key = (box.Text ?? "").Trim();
        (bool Stored, IReadOnlyList<string> Refused) result;
        try { result = await Task.Run(() => Host.Core.SetEncryptionKey(id, key)); }   // opens the game's files
        catch (Exception ex) { result = (false, [ex.Message]); }
        if (result.Refused.Count > 0) await App.ShowAsync(Refused(result.Stored, result.Refused));
        return result.Stored;
    }

    /// <param name="lookupId">the game whose key the dialog offers to look up (<see cref="AddKeyLookup"/>); null: none</param>
    public static (ContentDialog Dialog, TextBox Box) Dialog(string title, string? reason, bool aes, string? lookupId = null, Action? lookedUp = null)
    {
        var box = new TextBox { PlaceholderText = aes ? MultiKeyHint : "0x followed by 64 hex digits", Classes = { "clearButton" }, AcceptsReturn = aes, MaxHeight = 160 };
        var panel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { TextWrapping = TextWrapping.Wrap, Text = (aes ? AesIntro : GameKeyIntro) +
                    (lookupId != null ? " Look the key up online, or paste it if you have it" : " If you have the key, paste it here") +
                    ": it's checked against the game's files and kept on this PC only." },
                new StackPanel { Spacing = 8, Children = { new TextBlock { Text = aes ? "AES key" : "Game key" }, box } },
            },
        };
        if (reason != null) panel.Children.Insert(0, new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog { Title = title, Content = panel, CloseButtonText = "Close", PrimaryButtonText = "Unlock", DefaultButton = ContentDialogButton.Close };
        if (lookupId != null) AddKeyLookup(lookupId, panel, box, dialog, lookedUp);
        return (dialog, box);
    }

    // "Look up key online": the user's imported keys, then the community's key list (the key itself is never shown). The
    // forum may answer with a browser check; the page saved from a browser is then read instead (and cached for every game).
    static void AddKeyLookup(string id, StackPanel panel, TextBox box, ContentDialog dialog, Action? lookedUp)
    {
        var lookup = new Button { Content = "Look up key online" };
        var open = new Button { Content = "Open list in browser" };
        var saved = new Button { Content = "Load saved page…" };
        var ring = new ProgressRing { IsActive = false, IsVisible = false, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { lookup, ring } });
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { open, saved } });
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "Caption" }, Text = SavedPageHint });
        panel.Children.Add(status);
        void Say(string text) => (status.Text, status.IsVisible) = (text, true);
        async Task LookUp(string? page)
        {
            (lookup.IsEnabled, saved.IsEnabled, ring.IsActive, ring.IsVisible) = (false, false, true, true);
            Say(page == null ? "Looking up the key…" : "Checking the saved page…");
            var done = false;   // a stage reported late doesn't cover the result
            KeyLookup r;
            try { r = await Host.Core.LookUpKeyAsync(id, page, new Progress<string>(m => { if (!done) status.Text = m; })); }
            catch (Exception ex) { r = new(KeyLookupOutcome.FetchFailed, $"The lookup failed: {ex.Message}"); }
            done = true;
            var unlocked = r.Outcome == KeyLookupOutcome.Unlocked;
            (ring.IsActive, ring.IsVisible, lookup.IsEnabled, saved.IsEnabled) = (false, false, !unlocked, !unlocked);
            Say(unlocked ? r.Message + " The game is checked again now." : r.Message);
            if (!unlocked) return;
            (box.IsEnabled, dialog.IsPrimaryButtonEnabled) = (false, false);
            lookedUp?.Invoke();
        }
        lookup.Click += async (_, _) => await LookUp(null);
        open.Click += async (_, _) =>
        {
            try { if (await App.Main.Launcher.LaunchUriAsync(new Uri(KeyCollection.DefaultUrl))) return; }
            catch (Exception ex) { Say($"Couldn't open the browser: {ex.Message}"); return; }
            Say($"Couldn't open the browser. The list is at {KeyCollection.DefaultUrl}");
        };
        saved.Click += async (_, _) =>
        {
            string? page = null;
            try
            {
                var picked = await App.Main.StorageProvider.OpenFilePickerAsync(new()
                {
                    Title = "Load saved page",
                    FileTypeFilter = [new("Web pages") { Patterns = ["*.html", "*.htm"] }, new("All files") { Patterns = ["*"] }],
                });
                if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path)
                {
                    (page, var tooLarge) = await Task.Run(() => KeyCollection.ReadSavedPage(path));
                    if (tooLarge != null) { Say(tooLarge); return; }
                }
            }
            catch (Exception ex) { Say($"Couldn't read that page: {ex.Message}"); return; }
            if (page != null) await LookUp(page);
        };
    }

    /// <summary>The lines that didn't work, each with why. <paramref name="stored"/>: the others were kept.</summary>
    public static ContentDialog Refused(bool stored, IReadOnlyList<string> refused)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.AddRange(refused.Select(r => new TextBlock { Text = Format.Sentence(r.TrimEnd('.')) + ".", TextWrapping = TextWrapping.Wrap }));
        return new ContentDialog { Title = stored ? "Some keys didn't work" : refused.Count > 1 ? "Those keys didn't work" : "That key didn't work", Content = panel, CloseButtonText = "OK" };
    }

    const string MultiKeyHint = "0x and 64 hex digits, one key per line";
    const string AesIntro = "SCSKiller couldn't find this game's key in its exe (protected exes hide it).";
    const string GameKeyIntro = "With this game's key, SCSKiller compiles it without a recording.";
    const string SavedPageHint = "If the forum asks for a browser check: open the list, save the page (Ctrl+S, 'Webpage, HTML only' is enough), then load it here.";
}
