using SCSKiller.Core.Vendors;

namespace SCSKiller.Core.App;

// Driver-cache housekeeping: the old driver's files before a compile for a new one, the whole cache on request, and what
// nothing installed uses (Settings' clean-up list). Deletes go key by key (AppCacheFiles.DeleteByKey): a key a running
// process holds (a game, a browser, Steam, Discord) is left whole and named.
public sealed partial class ScsKiller
{
    /// <summary>A warm whose record is reset: what the cache held is gone, the game is compiled again from the start.</summary>
    static void ForgetWarm(GameRecord rec)
    {
        if (!IsUnreached(rec)) rec.UnreachedWarm = null;   // a verdict on an older warm: with WarmedAt gone it would apply again
        (rec.WarmedAt, rec.WarmedDriverVersion, rec.WarmedDriverId, rec.LastWarmTime, rec.LastCacheGrowthBytes, rec.ResumeAt) = (null, null, null, null, null, 0);
        (rec.LastWarmFailed, rec.LastWarmSkipped, rec.LastWarmNeedsRecording, rec.LastWarmCrashed) = (null, null, null, null);
        (rec.WarmedCareful, rec.FirstLaunch, rec.WarmedFiles) = (false, null, null);
    }

    /// <summary>Noted at each evaluation: Settings' clean-up names a game no longer installed by them. True if changed.</summary>
    static bool NoteIdentity(Game g, GameRecord r)
    {
        if (r.GameName == g.Name && r.GameExe == g.ExePath) return false;
        (r.GameName, r.GameExe) = (g.Name, g.ExePath);
        return true;
    }

    string? _sinceNoted;            // the DriverId driver.json was last checked for
    DateTimeOffset _seenNoted;      // ...and when its SeenAt was last saved
    static readonly TimeSpan SeenEvery = TimeSpan.FromMinutes(30);   // SeenAt may be that much older: it only keeps more

    /// <summary>Keeps driver.json on the current driver, when it became current and when the previous one was last seen:
    /// the first one noted was already installed (since unknown), and so was another GPU's (upstream issue 43: a pick is
    /// no driver update); a later one of the same GPU is new now.</summary>
    void NoteDriverSince()
    {
        if (DriverId is not { } id || id == _sinceNoted && Clock() - _seenNoted < SeenEvery) return;
        try
        {
            var (was, now) = (Store.LoadDriverSince(), Clock());
            Store.SaveDriverSince(was?.Id == id ? was with { SeenAt = now }
                : was != null && SameAdapter(was.Id, id) ? new(id, now, now, was.SeenAt) : new(id, null, now));
            (_sinceNoted, _seenNoted) = (id, now);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"couldn't note the driver: {e.Message}"); }
    }

    /// <summary>Two DriverIds of the same adapter (vendor, device and subsystem ids): only a version change is a driver update.</summary>
    static bool SameAdapter(string a, string b) => string.Equals(a[..Math.Max(a.LastIndexOf(':'), 0)], b[..Math.Max(b.LastIndexOf(':'), 0)], StringComparison.OrdinalIgnoreCase);

    /// <summary>Upstream issue 74: a compile for a new driver appends to the game's cache files of the old one (NVIDIA keeps
    /// them, and its D3D12 file keeps its type and size across drivers), so the game's cache doubled and its 4 GiB file
    /// filled up. Before such a compile (warmed for another driver, not resuming this driver's, the game not running,
    /// Settings.ClearOldDriverCache) the game's driver-cache files last written before the driver became current go, key by
    /// key; files the game wrote since (it ran on the new driver) stay. A watched play that ended, or a launch that began,
    /// after the previous driver was last seen may have run on the new one: it moves the cut to its start; one before then
    /// ran on the old driver, and its files go. Previous driver's last sighting unknown: any play since the old warm moves
    /// the cut. With the driver's start unknown (it was there before driver.json), only a game not seen running since its
    /// warm is cleared, whole. Another GPU picked (upstream issue 43) is no driver update: nothing goes. Never a key another
    /// game shares. Once per driver; a cache that can't be read skips it, and the compile goes ahead.</summary>
    void ClearOldDriverFiles(Game game, GameRecord rec, GpuSnapshot snap)
    {
        if (!Settings.ClearOldDriverCache || AppCache is not { } cache || snap.Id is not { } id || rec.ResumeAt > 0 || rec.WarmedAt is not { } warmed
            || CurrentDriver(snap, rec.WarmedDriverId, rec.WarmedDriverVersion) || rec.OldDriverCleared == id) return;
        if (rec.WarmedDriverId is { } was && !SameAdapter(was, id))
        {
            Log?.Report($"{game.Name}: last compiled for another GPU: its cache is kept");
            return;
        }
        var keys = DriverKeys(game, rec).Keys;
        if (keys.Count == 0 || GameRunning(game)) return;
        if (SharedWith(game.Id, keys) is { Count: > 0 } shared)
        {
            Log?.Report($"{game.Name}: the previous driver's cache is kept: its keys are shared with {string.Join(", ", shared.Select(s => s.Name))}");
            return;
        }
        try
        {
            var d = Store.LoadDriverSince() is { } s && s.Id == id ? s : null;
            // the starts of runs that may have been on this driver: after the old warm, and after the previous driver was last seen
            var after = d is { Since: not null, PrevSeenAt: { } prev } && prev > warmed ? prev : warmed;
            var played = new[] { rec.LastPlay is { } p && p.To > after ? p.From : (DateTimeOffset?)null, rec.FirstLaunch?.At is { } at && at > after ? at : null }
                .OfType<DateTimeOffset>().ToList();
            DateTimeOffset cut;
            if (d is { Since: { } since }) cut = played.Append(since).Min();
            else if (played.Count == 0) cut = Clock();
            else
            {
                Log?.Report($"{game.Name}: the previous driver's cache is kept: the game ran since its last compile, maybe on this driver, which was installed before SCSKiller noted it");
                return;
            }
            var old = cache.AllFiles().Where(f => keys.Contains(f.Key)).Select(f => (File: new FileInfo(f.Path), f.Key))
                .Where(f => f.File.LastWriteTimeUtc < cut.UtcDateTime && f.File.CreationTimeUtc < cut.UtcDateTime).ToList();
            var done = AppCacheFiles.DeleteByKey(old);
            if (done.Files > 0) Log?.Report($"{game.Name}: deleted {Format.Bytes(done.Bytes)} of its driver cache from before driver {snap.Gpu.DriverVersion}");
            if (done.Kept.Count > 0) Log?.Report($"{game.Name}: {Format.Bytes(done.KeptBytes)} of the previous driver's cache is in use by {string.Join(", ", done.InUseBy)}: kept");
            else
            {
                rec.OldDriverCleared = id;
                Store.SaveGame(game.Id, rec);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{game.Name}: the previous driver's cache is kept: {e.Message}"); }
    }

    public (long Bytes, int Apps)? DriverCacheTotal()
    {
        if (AppCache is not { } cache) return null;
        long bytes = 0;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, key) in cache.AllFiles())
        {
            bytes += Length(path);
            keys.Add(key);
        }
        return (bytes, keys.Count);
    }

    /// <summary>Upstream issue 80. A compile that starts meanwhile holds its files open: they are kept like a running game's.</summary>
    public CacheDeletion ClearDriverCache()
    {
        if (AppCache is not { } cache) throw new InvalidOperationException($"the {Vendor.Gpu.Name} driver's shader cache isn't split per application: SCSKiller can't clear it");
        if (Compiling) throw new InvalidOperationException("a compile is in progress: clear the cache once it has finished");
        var done = AppCacheFiles.DeleteByKey(cache.AllFiles().Select(f => (new FileInfo(f.Path), f.Key)).ToList());
        Log?.Report($"driver shader cache cleared: {Format.Bytes(done.Bytes)} deleted" + (done.Kept.Count > 0 ? $", {Format.Bytes(done.KeptBytes)} in use by {string.Join(", ", done.InUseBy)} kept" : ""));
        foreach (var s in Games)
        {
            var rec = Store.LoadGame(s.Game.Id);
            if (rec.WarmedAt == null || !DriverKeys(s.Game, rec).Keys.Overlaps(done.Deleted)) continue;
            ForgetWarm(rec);
            Store.SaveGame(s.Game.Id, rec);
            Refresh(s.Game);
        }
        return done;
    }

    const string GoneCachePrefix = "cache|", GoneDataPrefix = "data|", VendorPrefix = "vendor|";

    /// <summary>Upstream issue 35. A game is gone when its folder under games\ isn't a listed game's and its exe (noted at its
    /// evaluations) isn't on disk. Its driver-cache keys that no listed game uses or would get (on NVIDIA the key is the exe
    /// name's: a listed game's exe of the same name would share it) are offered, suggested only when its exe is known and its
    /// drive connected (a game on an unplugged drive looks uninstalled).
    /// Its SCSKiller folder only when nothing of the recorder is tracked in it. The other vendor's caches are never
    /// suggested: another GPU of the PC (integrated graphics) may use them.</summary>
    public IReadOnlyList<CleanupItem> CleanupItems()
    {
        var items = new List<CleanupItem>();
        var listed = Games;
        if (listed.Count == 0) return items;   // before a scan every game would look gone
        var used = UsedKeys(listed);
        foreach (var (id, rec) in GoneGames(listed))
        {
            var name = rec.GameName ?? id;
            var gone = rec.GameExe != null && !Unplugged(rec.GameExe);
            if (AppCache != null && GoneKeys(rec, used) is { Count: > 0 } keys && AppCache.SizeOf(keys) is > 0 and var bytes)
                items.Add(new(GoneCachePrefix + id, $"{name}: driver cache", CleanupKind.GoneGameCache, bytes, gone,
                    gone ? "The game isn't installed any more."
                    : rec.GameExe != null ? $"Its drive ({Path.GetPathRoot(rec.GameExe)}) isn't connected: if the game is still on it, it compiles again later."
                    : "Not seen by this version of SCSKiller: if the game is on a drive that isn't connected, it compiles again later."));
            if (!Tracked(rec) && FolderBytes(Store.GameDir(id)) is > 0 and var data)
                items.Add(new(GoneDataPrefix + id, $"{name}: SCSKiller's data", CleanupKind.GoneGameData, data, false,
                    "Its plan and recording. A reinstall plans again and records from the start."));
        }
        foreach (var (vendor, cache) in OtherVendorCaches())
            if (cache.AllFiles().Sum(f => Length(f.Path)) is > 0 and var bytes)
                items.Add(new(VendorPrefix + vendor.ToString().ToLowerInvariant(), $"{(vendor == GpuVendor.Nvidia ? "NVIDIA" : "AMD")} driver cache", CleanupKind.OtherVendorCache, bytes, false,
                    $"Not this GPU's. {(Adapters?.Invoke().FirstOrDefault(a => a.Gpu.Vendor == vendor) is { } other ? $"This PC also has {other.Gpu.Name}, which uses it" : "Another GPU or an app set to one may use it")}: what it compiled is compiled again."));
        return items;
    }

    public CacheDeletion CleanUp(string itemId)
    {
        if (Compiling) throw new InvalidOperationException("a compile is in progress: clean up once it has finished");
        var item = CleanupItems().FirstOrDefault(i => i.Id == itemId) ?? throw new InvalidOperationException("it isn't in the clean-up list any more");
        var arg = item.Id[(item.Id.IndexOf('|') + 1)..];
        switch (item.Kind)
        {
            case CleanupKind.GoneGameCache:
                var keys = GoneKeys(Store.LoadGame(arg), UsedKeys(Games));
                return AppCacheFiles.DeleteByKey(AppCache!.AllFiles().Where(f => keys.Contains(f.Key)).Select(f => (new FileInfo(f.Path), f.Key)).ToList());
            case CleanupKind.GoneGameData:
                var dir = Store.GameDir(arg);
                var files = new DirectoryInfo(dir).GetFiles("*", SearchOption.AllDirectories);
                var bytes = files.Sum(f => f.Length);
                Directory.Delete(dir, true);
                return new(new HashSet<string>(), files.Length, bytes, new HashSet<string>(), 0, []);
            default:
                var cache = OtherVendorCaches().First(c => VendorPrefix + c.Vendor.ToString().ToLowerInvariant() == item.Id).Cache;
                return AppCacheFiles.DeleteByKey(cache.AllFiles().Select(f => (new FileInfo(f.Path), f.Key)).ToList());
        }
    }

    /// <summary>The records under games\ of games not listed now whose exe isn't on disk (unknown: an earlier build's record),
    /// by an id whose <see cref="AppStore.GameDir"/> is their folder (a plain folder name stands for itself).</summary>
    IEnumerable<(string Id, GameRecord Rec)> GoneGames(IReadOnlyList<GameState> listed)
    {
        var dir = Path.Combine(Store.DataDir, "games");
        if (!Directory.Exists(dir)) yield break;
        var folders = listed.Select(s => Path.GetFileName(Store.GameDir(s.Game.Id))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Directory.EnumerateDirectories(dir))
        {
            var id = AppStore.GameId(Path.GetFileName(folder));
            if (folders.Contains(Path.GetFileName(folder)) || !File.Exists(Path.Combine(folder, "state.json"))) continue;
            GameRecord rec;
            try { rec = Store.LoadGame(id); }
            catch (ArgumentException) { continue; }   // not a game id's folder
            if (rec.GameExe == null || !File.Exists(rec.GameExe)) yield return (id, rec);
        }
    }

    /// <summary>The path's drive (or share) isn't there or isn't ready: an external or second drive not connected now.</summary>
    static bool Unplugged(string path)
    {
        if (Path.GetPathRoot(path) is not { Length: > 0 } root) return true;
        try { return !Directory.Exists(root) || root.Length <= 3 && !new DriveInfo(root).IsReady; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return true; }
    }

    /// <summary>The driver-cache keys the listed games use or would get, and their exe file names (both cases seen).</summary>
    (HashSet<string> Keys, HashSet<string> Exes) UsedKeys(IReadOnlyList<GameState> listed)
    {
        var (keys, exes) = (new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        foreach (var s in listed)
        {
            var r = Store.LoadGame(s.Game.Id);
            keys.UnionWith(DriverKeys(s.Game, r).Keys);
            keys.Add(AmdAppCache.HintKey(WarmExeName(s.Game, r)));
            exes.Add(Path.GetFileName(s.Game.ExePath));
            if (r.LaunchedExeName != null) exes.Add(r.LaunchedExeName);
        }
        return (keys, exes);
    }

    /// <summary>The gone game's keys no listed game uses; on NVIDIA none when a listed game's exe has its exe's name (the key
    /// is the name's).</summary>
    IReadOnlySet<string> GoneKeys(GameRecord rec, (HashSet<string> Keys, HashSet<string> Exes) used) =>
        AppCache is NvidiaAppCache && rec.GameExe != null && used.Exes.Contains(Path.GetFileName(rec.GameExe)) ? new HashSet<string>()
            : rec.CacheKeys.Where(k => !used.Keys.Contains(k)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The record still tracks the recorder in a game folder: its files are taken out only through it.</summary>
    static bool Tracked(GameRecord r) =>
        r.RecorderFiles.Count > 0 || r.RecorderExe != null || r.RecorderChained != null || r.RecorderMoveFrom != null || r.RecorderMoveTo != null || r.OfflineSession != null;

    /// <summary>The per-application driver caches of the vendors other than this GPU's, in %LOCALAPPDATA%, but this one's own folder.</summary>
    IEnumerable<(GpuVendor Vendor, IAppCache Cache)> OtherVendorCaches()
    {
        string[] own = AppCache switch { NvidiaAppCache nv => [nv.Dir], AmdAppCache amd => [amd.DxcDir, amd.DxDir], _ => [] };
        bool Own(string d) => own.Any(o => string.Equals(Path.GetFullPath(o), Path.GetFullPath(d), StringComparison.OrdinalIgnoreCase));
        var nvidia = Path.Combine(LocalAppData, "NVIDIA", "DXCache");
        var (dxc, dx) = (Path.Combine(LocalAppData, "AMD", "DxcCache"), Path.Combine(LocalAppData, "AMD", "DxCache"));
        if (Vendor.Vendor != GpuVendor.Nvidia && !Own(nvidia)) yield return (GpuVendor.Nvidia, new NvidiaAppCache(nvidia));
        if (Vendor.Vendor != GpuVendor.Amd && !Own(dxc) && !Own(dx)) yield return (GpuVendor.Amd, new AmdAppCache(dxc, dx));
    }

    static long FolderBytes(string dir)
    {
        try { return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }
}
