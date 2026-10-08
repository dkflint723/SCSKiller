using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Tests.Platform;

// Driver-cache housekeeping on synthetic cache folders under %TEMP% (never the real NVIDIA or AMD cache): the old driver's
// files before a compile for a new one (upstream issue 74), the whole cache (80), the clean-up list (35), the GPU choice (43).
public partial class AppTests
{
    /// <summary>A cache file last written (and created) at <paramref name="at"/>.</summary>
    string CacheFileAt(string name, DateTime at)
    {
        var path = CacheFile(name, 4096);
        File.SetCreationTimeUtc(path, at);
        File.SetLastWriteTimeUtc(path, at);
        return path;
    }

    /// <summary>The fixture's game warmed on driver 100.01 with its key 33333333 learned, then the driver updated to 101.00.</summary>
    async Task<(ScsKiller K, Func<GpuInfo> Adapter)> WarmedThenUpdated(Action<ScsKiller>? before = null)
    {
        var adapter = Gpu;
        var k = Killer(new FakeReader(Unreal));
        k.Adapters = () => [Listed(adapter)];
        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));
        before?.Invoke(k);
        await k.ScanAsync(default);
        await WarmOnce(k, _game.Id);
        var rec = k.Store.LoadGame(_game.Id);
        rec.CacheKeys.Add("33333333");
        k.Store.SaveGame(_game.Id, rec);
        adapter = Gpu with { DriverVersion = "101.00", AdapterLuid = 2 };
        k.RedetectGpu();
        return (k, () => adapter);
    }

    [Fact]
    public async Task A_compile_for_a_new_driver_first_deletes_the_games_files_from_before_it_and_keeps_what_came_since()
    {
        var (k, _) = await WarmedThenUpdated();
        Assert.Equal(new DriverSince("0000:0010:00000020:101.00", null), k.Store.LoadDriverSince()! with { Since = null });
        Assert.NotNull(k.Store.LoadDriverSince()!.Since);   // a change seen: since now
        var old = DateTime.UtcNow.AddDays(-2);
        string[] before = [CacheFileAt("33cba91d33333333.nvph", old), CacheFileAt("0002a91d33333333.nvph", old)];
        var since = CacheFileAt("408da91d33333333.nvph", DateTime.UtcNow.AddMinutes(5));   // the game ran on the new driver
        var other = CacheFileAt("0002a91d44444444.nvph", old);

        await WarmOnce(k, _game.Id);
        Assert.All(before, f => Assert.False(File.Exists(f)));
        Assert.True(File.Exists(since));
        Assert.True(File.Exists(other));
        Assert.Equal("0000:0010:00000020:101.00", k.Store.LoadGame(_game.Id).OldDriverCleared);

        var again = CacheFileAt("33cba91d33333333.nvph", old);   // the next compile on this driver deletes nothing
        await WarmOnce(k, _game.Id);
        Assert.True(File.Exists(again));
    }

    [Fact]
    public async Task The_old_drivers_cache_stays_with_the_setting_off()
    {
        var (k, _) = await WarmedThenUpdated(k => k.Settings = k.Settings with { ClearOldDriverCache = false });
        var file = CacheFileAt("0002a91d33333333.nvph", DateTime.UtcNow.AddDays(-2));
        await WarmOnce(k, _game.Id);
        Assert.True(File.Exists(file));
        Assert.Null(k.Store.LoadGame(_game.Id).OldDriverCleared);
    }

    /// <summary>A driver already there when SCSKiller first noted one: when it came is unknown, so a game seen running since
    /// its compile keeps everything, and one not seen is cleared whole.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_the_drivers_start_unknown_only_a_game_not_seen_running_since_its_compile_is_cleared(bool played)
    {
        var (k, _) = await WarmedThenUpdated();
        k.Store.SaveDriverSince(new("0000:0010:00000020:101.00", null));
        var rec = k.Store.LoadGame(_game.Id);
        if (played) rec.LastPlay = new(DateTimeOffset.Now, DateTimeOffset.Now.AddSeconds(1));   // after its compile
        k.Store.SaveGame(_game.Id, rec);
        var recent = CacheFileAt("408da91d33333333.nvph", DateTime.UtcNow.AddMinutes(-20));

        await WarmOnce(k, _game.Id);
        Assert.Equal(played, File.Exists(recent));
    }

    [Fact]
    public async Task Clearing_the_whole_driver_cache_keeps_what_a_process_holds_and_resets_the_games_it_cleared()
    {
        var k = await Warmed();
        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));
        var rec = k.Store.LoadGame(_game.Id);
        rec.CacheKeys.Add("33333333");
        k.Store.SaveGame(_game.Id, rec);
        string[] gone = [CacheFile("0002a91d33333333.nvph", 65536), CacheFile("fc52a91d33333333.nvph", 4096), CacheFile("0002a91d44444444.nvph", 8192)];
        var held = CacheFile("0002a91d55555555.nvph", 16384);
        var heldToo = CacheFile("fc52a91d55555555.nvph", 4096);   // same key: kept with it
        Assert.Equal((65536L + 4096 + 8192 + 16384 + 4096, 3), k.DriverCacheTotal());

        CacheDeletion done;
        using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))   // open, like a browser's
            done = k.ClearDriverCache();
        Assert.All(gone, f => Assert.False(File.Exists(f)));
        Assert.True(File.Exists(held) && File.Exists(heldToo));
        Assert.Equal((3, 65536L + 4096 + 8192), (done.Files, done.Bytes));
        Assert.Equal(["55555555"], done.Kept);
        Assert.Equal(16384L + 4096, done.KeptBytes);
        Assert.Equal([Process.GetCurrentProcess().ProcessName + ".exe"], done.InUseBy);
        Assert.Equal((GameStatus.Ready, null), (k.Games.Single().Status, k.Games.Single().WarmedAt));
        Assert.Equal(["33333333"], k.Store.LoadGame(_game.Id).CacheKeys);   // the exe name's key: still the game's

        k.AppCache = null;
        Assert.Null(k.DriverCacheTotal());
        Assert.Throws<InvalidOperationException>(() => k.ClearDriverCache());
    }

    [Fact]
    public async Task The_clean_up_list_offers_caches_of_games_no_longer_installed_and_of_the_other_vendor()
    {
        var k = Killer(new FakeReader(Unreal));
        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));
        Assert.Empty(k.CleanupItems());   // before a scan every game would look gone
        await k.ScanAsync(default);
        var listed = k.Store.LoadGame(_game.Id);
        Assert.Equal(("Fake Game", _game.ExePath), (listed.GameName, listed.GameExe));   // noted at its evaluation
        listed.CacheKeys.Add("33333333");
        k.Store.SaveGame(_game.Id, listed);

        GameRecord Gone(string id, string exe, params string[] keys)
        {
            var r = new GameRecord { GameName = id, GameExe = Path.Combine(_root, "Uninstalled", exe) };
            r.CacheKeys.UnionWith(keys);
            k.Store.SaveGame(id, r);
            File.WriteAllBytes(Path.Combine(k.Store.GameDir(id), "plan.bin"), new byte[1000]);
            return r;
        }
        Gone("steam:1", "gone.exe", "55555555", "33333333");   // 33333333 is the listed game's too
        Gone("steam:2", "Fake-Win64-Shipping.exe", "66666666");   // the listed game's exe name: NVIDIA's key would be the same
        var tracked = Gone("steam:3", "other.exe", "77777777");
        tracked.RecorderExe = Path.Combine(_root, "Uninstalled", "other.exe");   // its recorder is tracked here: the data stays
        k.Store.SaveGame("steam:3", tracked);
        var mine = CacheFile("0002a91d33333333.nvph", 4096);
        var gone = CacheFile("0002a91d55555555.nvph", 8192);
        CacheFile("0002a91d66666666.nvph", 4096);
        CacheFile("0002a91d77777777.nvph", 2048);
        var amd = Path.Combine(_root, "AMD", "DxcCache", "207c35a9.dfac411e.71efbc0e.2b1a674a.0.parc");
        Directory.CreateDirectory(Path.GetDirectoryName(amd)!);
        File.WriteAllBytes(amd, new byte[3000]);

        var items = k.CleanupItems();
        Assert.Equal(["cache|steam_1", "data|steam_1", "data|steam_2", "cache|steam_3", "vendor|amd"], items.Select(i => i.Id));   // by folder
        Assert.Equal("steam:1: driver cache", items[0].Name);
        Assert.Equal((8192L, true), (items[0].Bytes, items[0].Suggested));
        Assert.Equal((3000L, false), (items[^1].Bytes, items[^1].Suggested));
        Assert.All(items.Where(i => i.Kind == CleanupKind.GoneGameData), i => Assert.False(i.Suggested));

        var done = k.CleanUp("cache|steam_1");
        Assert.Equal((1, 8192L), (done.Files, done.Bytes));
        Assert.False(File.Exists(gone));
        Assert.True(File.Exists(mine));
        k.CleanUp("data|steam_1");
        Assert.False(Directory.Exists(k.Store.GameDir("steam:1")));
        k.CleanUp("vendor|amd");
        Assert.False(File.Exists(amd));
        Assert.Equal(["data|steam_2", "cache|steam_3"], k.CleanupItems().Select(i => i.Id));
        Assert.Throws<InvalidOperationException>(() => k.CleanUp("cache|steam_1"));   // no longer listed

        Directory.CreateDirectory(Path.Combine(_root, "Uninstalled"));
        File.WriteAllBytes(Path.Combine(_root, "Uninstalled", "here.exe"), []);
        Gone("steam:4", "here.exe", "88888888");   // its exe is on disk: maybe a game this scan missed, not offered
        CacheFile("0002a91d88888888.nvph", 4096);
        Assert.DoesNotContain(k.CleanupItems(), i => i.Id.EndsWith("steam_4"));
    }

    static DxgiAdapter Adapter(GpuVendor vendor, string name, ulong vram, uint device, uint subsys, long luid) =>
        new(new GpuInfo(vendor, name, "1.0", luid, vram), device, subsys);

    [Fact]
    public void A_picked_gpu_is_one_of_the_vendors_by_its_pci_ids_and_an_unknown_pick_falls_back_to_the_most_vram()
    {
        var a = Adapter(GpuVendor.Nvidia, "RTX 4090", 24UL << 30, 0x2684, 0x16f310de, 1);
        var b = Adapter(GpuVendor.Nvidia, "RTX 3090", 24UL << 29, 0x2204, 0x147d10de, 2);
        var twin = Adapter(GpuVendor.Nvidia, "RTX 3090", 24UL << 29, 0x2204, 0x147d10de, 3);
        var igpu = Adapter(GpuVendor.Amd, "AMD Radeon(TM) Graphics", 512UL << 20, 0x13c0, 0x8a1e1043, 4);
        DxgiAdapter[] all = [igpu, b, a, twin];

        var choices = GpuBackends.Choices(all);
        Assert.Equal(["10de:2204:147d10de", "10de:2684:16f310de", "10de:2204:147d10de#1"], choices.Select(c => c.Id));   // DXGI's order; no AMD iGPU
        Assert.Same(a, GpuBackends.Chosen(all, null));
        Assert.Same(b, GpuBackends.Chosen(all, "10DE:2204:147D10DE"));
        Assert.Same(twin, GpuBackends.Chosen(all, "10de:2204:147d10de#1"));
        Assert.Same(a, GpuBackends.Chosen(all, "10de:9999:00000000"));   // not here any more
        Assert.Same(a, GpuBackends.Chosen(all, "1002:13c0:8a1e1043"));   // another vendor's: never
        Assert.Equal("RTX 3090", GpuBackends.Primary(all, "10de:2204:147d10de")!.Name);
        Assert.Empty(GpuBackends.Choices([]));
    }

    [Fact]
    public void Picking_another_gpu_while_running_asks_for_a_restart()
    {
        var k = Killer();
        var main = Listed(Gpu with { DedicatedVideoMemory = 2 });
        var second = new DxgiAdapter(Gpu with { Name = "Second GPU", AdapterLuid = 7, DedicatedVideoMemory = 1 }, 0x11, 0x20);
        k.Adapters = () => [main, second];
        k.RedetectGpu();
        Assert.Null(k.GpuRestartNote);

        k.Settings = k.Settings with { GpuAdapter = GpuBackends.AdapterId(second) };
        Assert.True(k.RedetectGpu());
        Assert.Equal("Restart SCSKiller to use Second GPU", k.GpuRestartNote);
        k.Settings = k.Settings with { GpuAdapter = null };
        k.RedetectGpu();
        Assert.Null(k.GpuRestartNote);
    }
}
