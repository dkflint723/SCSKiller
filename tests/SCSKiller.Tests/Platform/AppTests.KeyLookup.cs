using System.Net;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Tests.Platform;

// The automatic key lookup after a scan (Settings.LookUpKeysOnline): off by default, then once per game and list.
public partial class AppTests
{
    static readonly EngineInfo EncryptedUnreal = new("Unreal", "5.4", null, "D3D12", true, "encrypted game files (needs the game's AES key)");

    sealed class KeyPage : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent($"<div class=\"postbody\">Fake Game&nbsp;0x{new string('A', 32)}{new string('5', 32)}</div>", Encoding.UTF8, "text/html") });
        }
    }

    [Fact]
    public async Task Key_lookups_are_off_by_default_and_then_run_once_per_game_after_a_scan()
    {
        var page = new KeyPage();
        var reader = new FakeReader(EncryptedUnreal);
        var k = Killer(reader);
        var tries = 0;
        k.KeyList = new KeyCollection(Path.Combine(_root, "data"), page);
        k.KeyCheck = g => ([g.Name], _ => { Interlocked.Increment(ref tries); return false; });
        Assert.False(k.Settings.LookUpKeysOnline);
        await k.ScanAsync(default);
        await k.KeyLookupPass;
        Assert.Equal((0, 0), (page.Requests, tries));   // off: nothing fetched, nothing tried

        k.Settings = k.Settings with { LookUpKeysOnline = true };   // turning it on looks up the listed games
        await k.KeyLookupPass;
        Assert.Equal((1, 1), (page.Requests, tries));
        await k.ScanAsync(default);
        await k.KeyLookupPass;
        Assert.Equal((1, 1), (page.Requests, tries));   // the same candidates for the same exe: not again, and the list is fresh

        k.KeyCheck = g => ([g.Name], _ => { Interlocked.Increment(ref tries); return true; });
        var detects = reader.Detects;
        var r = await k.LookUpKeyAsync(_game.Id);   // the user's button tries again
        Assert.Equal((KeyLookupOutcome.Unlocked, "Fake Game", 2), (r.Outcome, r.Entry, tries));
        Assert.Equal(detects, reader.Detects);   // the caller rescans (as after a pasted key)
    }

    [Fact]
    public async Task A_saved_page_loaded_for_one_game_serves_the_other_encrypted_games()
    {
        var install = Path.Combine(_root, "Other");
        Directory.CreateDirectory(install);
        var other = new Game("test:other", "Other Game", Store.Other, install, Path.Combine(install, "Other.exe"));
        var page = new KeyPage();
        var k = Killer(new FakeReader(EncryptedUnreal), games: [_game, other]);
        var tried = new List<string>();
        k.KeyList = new KeyCollection(Path.Combine(_root, "data"), page);
        k.KeyCheck = g => ([g.Name], key => { lock (tried) tried.Add(g.Name); return false; });
        await k.ScanAsync(default);
        k.Settings = k.Settings with { LookUpKeysOnline = true };
        await k.KeyLookupPass;   // the fetched list has only this game's old key
        var saved = $"<div class=\"postbody\">Intro<br>Other Game 0x{new string('C', 64)}<br>Fake Game 0x{new string('D', 64)}</div>";
        var r = await k.LookUpKeyAsync(_game.Id, saved);
        await k.KeyLookupPass;
        Assert.Equal(KeyLookupOutcome.NoWorkingKey, r.Outcome);
        Assert.Equal(["Fake Game", "Fake Game", "Other Game"], tried.Order());   // the other game from the saved copy
        Assert.Equal(1, page.Requests);   // the saved copy is the fresh list
    }

    [Fact]
    public async Task A_scan_never_looks_up_keys_of_unencrypted_games()
    {
        var page = new KeyPage();
        var k = Killer(new FakeReader(Unreal));
        k.KeyList = new KeyCollection(Path.Combine(_root, "data"), page);
        k.KeyCheck = g => throw new InvalidOperationException("not looked up");
        k.Settings = k.Settings with { LookUpKeysOnline = true };
        await k.ScanAsync(default);
        await k.KeyLookupPass;
        Assert.Equal(0, page.Requests);
    }
}
