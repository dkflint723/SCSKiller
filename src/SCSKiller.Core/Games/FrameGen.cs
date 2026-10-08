using SCSKiller.Core.Planning;

namespace SCSKiller.Core.Games;

/// <summary>Frame generation that wraps the game's swap chain and calls NVAPI, found by its files in the exe's folder or the
/// install root (top level only): NVIDIA Streamline with DLSS-G, Nukem's DLSS-G to FSR3 mod, OptiScaler's frame generation.
/// The files ship whether it's on or not, so they take no recorder hook off by themselves (the crash guard does); only
/// where the recorder isn't needed does it start at pipelines only (ScsKiller.StartLevel): with the frame-timing and NVAPI
/// hooks, FINAL FANTASY XVI with DLSS-G on crashed in NVIDIA's driver 4 s in. Frame generation the game doesn't ship (Lossless
/// Scaling, the driver's Smooth Motion) runs outside its folder and isn't seen.</summary>
public static class FrameGen
{
    // a label and the file sets that mark it: every file of one set (one * matches any run of characters)
    static readonly (string Label, string[][] Sets)[] Rules =
    [
        ("Streamline DLSS-G", [["sl.interposer.dll", "sl.dlss_g.dll"], ["nvngx_dlssg.dll"]]),
        ("DLSS-G to FSR3 mod", [["dlssg_to_fsr3*.dll"]]),
    ];

    // OptiScaler.ini [FrameGen] FGType values that generate frames; "auto" (or none) does when an FG library is there
    static readonly string[] OptiFg = ["optifg", "nukems", "xefg"];
    static readonly string[] OptiFgLibraries = ["amd_fidelityfx_framegeneration_dx12.dll", "libxess_fg.dll"];

    /// <summary>What generates frames there ("Streamline DLSS-G"); null = nothing known. Never call it for an anti-cheat install.</summary>
    public static string? Detect(string exeDir, string? installRoot)
    {
        foreach (var dir in new[] { exeDir, installRoot }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string[] files;
            try { files = Directory.Exists(dir) ? [.. Directory.EnumerateFiles(dir, "*.dll", new EnumerationOptions { IgnoreInaccessible = true }).Select(Path.GetFileName).OfType<string>()] : []; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            bool Has(string pattern) => files.Any(f => pattern.Split('*') is var p && p.Length == 2
                ? f.Length >= p[0].Length + p[1].Length && f.StartsWith(p[0], StringComparison.OrdinalIgnoreCase) && f.EndsWith(p[1], StringComparison.OrdinalIgnoreCase)
                : f.Equals(pattern, StringComparison.OrdinalIgnoreCase));
            foreach (var (label, sets) in Rules)
                if (sets.Any(s => s.All(Has))) return label;
            if (Middleware.Detect(dir).Any(d => d.Vendor == "optiscaler") && OptiScalerGenerates(dir, OptiFgLibraries.Any(Has))) return "OptiScaler frame generation";
        }
        return null;
    }

    /// <summary>As the proxy's opti_fg reads OptiScaler.ini: a newer one's [FrameGen] FGOutput (with Enabled=true) names the
    /// output, else FGType names the kind; [OptiFG] Enabled=true is an older one's switch.</summary>
    internal static bool OptiScalerGenerates(string dir, bool library)
    {
        var ini = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // "section.key" -> the last value
        try
        {
            var path = Path.Combine(dir, "OptiScaler.ini");
            var section = "";
            if (File.Exists(path))
                foreach (var l in File.ReadLines(path).Select(l => l.Split(';')[0].Trim()))
                    if (l.StartsWith('[') && l.EndsWith(']')) section = l[1..^1].Trim();
                    else if (l.IndexOf('=') is > 0 and var eq) ini[$"{section}.{l[..eq].Trim()}"] = l[(eq + 1)..].Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }   // unread: taken as on
        bool Is(string key, string value) => ini.TryGetValue(key, out var v) && v.Equals(value, StringComparison.OrdinalIgnoreCase);
        if (ini.TryGetValue("FrameGen.FGOutput", out var output) && output.Length > 0)
            return Is("FrameGen.Enabled", "true") && !output.Equals("auto", StringComparison.OrdinalIgnoreCase) && !output.Equals("nofg", StringComparison.OrdinalIgnoreCase);
        var type = ini.GetValueOrDefault("FrameGen.FGType") ?? ini.Where(x => x.Key.EndsWith(".FGType", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).LastOrDefault();
        if (Is("OptiFG.Enabled", "true") && !"nofg".Equals(type, StringComparison.OrdinalIgnoreCase)) return true;
        return type is null or "" || type.Equals("auto", StringComparison.OrdinalIgnoreCase) ? library : OptiFg.Contains(type, StringComparer.OrdinalIgnoreCase);
    }
}
