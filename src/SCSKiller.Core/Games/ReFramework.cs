using System.Collections.Concurrent;

namespace SCSKiller.Core.Games;

/// <summary>REFramework (praydog's RE Engine mod loader, as dinput8.dll next to the exe). In DD2, MHRise and RE Engine
/// games of TDB 74 and later it copies every *.dll next to the exe into <see cref="Storage"/> at each launch, overwriting,
/// never deleting one (REFramework.cpp), and points each loaded dll's path in the loader's list at its copy there
/// (kananlib's spoof_module_paths_in_exe_dir). The copies are never run: the dlls still run from the exe's folder, under
/// a path that names the copy (the recorder finds its game folder through that, proxy.cpp's DllMain).</summary>
public static class ReFramework
{
    /// <summary>The folder next to the exe REFramework keeps its copies in.</summary>
    public const string Storage = "_storage_";

    // its own name, and the folder it copies into (its build from before the copies has no reason to be told apart)
    static readonly byte[][] Markers = ["REFramework"u8.ToArray(), "_storage_"u8.ToArray()];
    const long MaxBytes = 128L << 20;
    static readonly ConcurrentDictionary<string, (long Length, DateTime Written, bool Found)> Probed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>REFramework that keeps copies in <see cref="Storage"/> is the dinput8.dll next to the exe: its bytes name it
    /// and the folder. Each dinput8.dll is read once while its size and write time stay the same.</summary>
    public static bool Detect(string exeDir)
    {
        var f = new FileInfo(Path.Combine(exeDir, "dinput8.dll"));
        if (!f.Exists || f.Length > MaxBytes) return false;
        if (Probed.TryGetValue(f.FullName, out var c) && (c.Length, c.Written) == (f.Length, f.LastWriteTimeUtc)) return c.Found;
        try
        {
            using var s = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[s.Length];
            s.ReadExactly(bytes);
            var found = Array.TrueForAll(Markers, m => bytes.AsSpan().IndexOf(m) >= 0);
            Probed[f.FullName] = (f.Length, f.LastWriteTimeUtc, found);
            return found;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
