using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Ui.Models;

/// <summary>A game's icon as image file bytes, read without the Windows shell: a packaged (Xbox / Game Pass) game's own
/// logo, else the first icon in the exe's resources as an .ico file. Null when there is none or it can't be read.</summary>
public static partial class GameIcon
{
    /// <summary>The icon size picked from the exe: the smallest at least this wide, else the largest.</summary>
    const int Wanted = 64;
    const uint IconType = 3, GroupIconType = 14;

    public static byte[]? Read(string exePath)
    {
        try
        {
            if (PackageLogo(exePath) is { } logo) return File.ReadAllBytes(logo);
            if (!File.Exists(exePath)) return null;
            using var exe = File.OpenHandle(exePath);   // the parser reads the PE headers and the resource tree by offset, not the whole exe (tens of MB on a big game)
            return ExeIcon(exe, RandomAccess.GetLength(exe));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The Square44x44Logo of the package the exe sits in (appxmanifest.xml in a parent folder): a GDK game's exe
    /// often has no icon of its own (Atomic Heart). Null when not packaged, unreadable or the logo is only there scale-qualified.</summary>
    static string? PackageLogo(string exePath)
    {
        try
        {
            for (var dir = Path.GetDirectoryName(exePath); dir != null; dir = Path.GetDirectoryName(dir))
            {
                var manifest = Path.Combine(dir, "appxmanifest.xml");
                if (!File.Exists(manifest)) continue;
                var m = Square44().Match(File.ReadAllText(manifest));
                return m.Success && Path.Combine(dir, m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar)) is var logo && File.Exists(logo) ? logo : null;
            }
        }
        catch (Exception) { }
        return null;
    }

    [GeneratedRegex("Square44x44Logo=\"([^\"]+)\"")]
    private static partial Regex Square44();

    /// <summary>The exe's first icon group (the one Explorer shows) as an .ico file with the one picked image.</summary>
    public static byte[]? ExeIcon(SafeFileHandle pe, long length)
    {
        try
        {
            var r = new Resources(pe, length);
            if (r.First(GroupIconType) is not { } group || group.Length < 6) return null;
            int count = U16(group, 4);
            int best = -1, bestWidth = 0, bestBits = 0;
            for (int i = 0; i < count && 6 + 14 * (i + 1) <= group.Length; i++)
            {
                int e = 6 + 14 * i, width = group[e] == 0 ? 256 : group[e], bits = U16(group, e + 6);
                bool better = best < 0
                    || (width >= Wanted ? bestWidth < Wanted || width < bestWidth : bestWidth < Wanted && width > bestWidth)
                    || width == bestWidth && bits > bestBits;
                if (better) (best, bestWidth, bestBits) = (i, width, bits);
            }
            if (best < 0) return null;
            int entry = 6 + 14 * best;
            if (r.Find(IconType, U16(group, entry + 12)) is not { } image) return null;
            // ICONDIR, one ICONDIRENTRY (the group's entry with the image's file offset in place of its resource id), the image
            var ico = new byte[22 + image.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4), 1);
            group.AsSpan(entry, 8).CopyTo(ico.AsSpan(6));
            BinaryPrimitives.WriteInt32LittleEndian(ico.AsSpan(14), image.Length);
            BinaryPrimitives.WriteInt32LittleEndian(ico.AsSpan(18), 22);
            image.CopyTo(ico, 22);
            return ico;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException) { return null; }   // a damaged or truncated file
    }

    static int U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
    static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));

    /// <summary>The PE resource tree: type, then name or id, then language; offsets in it are from the tree's start.</summary>
    sealed class Resources
    {
        readonly SafeFileHandle pe;
        readonly long length;
        readonly (uint Rva, uint Size, uint Raw)[] sections;
        readonly int root;

        public Resources(SafeFileHandle pe, long length)
        {
            this.pe = pe;
            this.length = length;
            int nt = (int)U32(0x3C);
            if (U32(nt) != 0x4550) throw new ArgumentException("not a PE file");
            int count = U16(nt + 6), optional = nt + 24, optionalSize = U16(nt + 20);
            int directories = optional + (U16(optional) == 0x20B ? 112 : 96);
            sections = [.. Enumerable.Range(0, count).Select(i => optional + optionalSize + 40 * i).Select(s => (U32(s + 12), Math.Max(U32(s + 8), U32(s + 16)), U32(s + 20)))];
            root = Offset(U32(directories + 2 * 8)) ?? throw new ArgumentException("no resources");
        }

        readonly byte[] window = new byte[8192];   // the tree's fields are small and close together: one read serves many of them
        long windowAt;
        int windowLength;

        /// <summary>Fills <paramref name="into"/> from the file at <paramref name="at"/>; reading past the end throws like the array it replaces.</summary>
        void Read(int at, Span<byte> into)
        {
            if (at < 0 || at + (long)into.Length > length) throw new ArgumentOutOfRangeException(nameof(at));
            if (into.Length > window.Length) { Fill(into, at); return; }
            if (at < windowAt || at + into.Length > windowAt + windowLength)
            {
                windowAt = at;
                windowLength = (int)Math.Min(window.Length, length - at);
                Fill(window.AsSpan(0, windowLength), at);
            }
            window.AsSpan((int)(at - windowAt), into.Length).CopyTo(into);
        }

        void Fill(Span<byte> into, long at)
        {
            for (int done = 0, n; done < into.Length; done += n)
                if ((n = RandomAccess.Read(pe, into[done..], at + done)) == 0) throw new ArgumentOutOfRangeException(nameof(at));   // the file shrank
        }

        int U16(int at) { Span<byte> b = stackalloc byte[2]; Read(at, b); return BinaryPrimitives.ReadUInt16LittleEndian(b); }
        uint U32(int at) { Span<byte> b = stackalloc byte[4]; Read(at, b); return BinaryPrimitives.ReadUInt32LittleEndian(b); }

        int? Offset(uint rva) => sections.FirstOrDefault(s => rva >= s.Rva && rva < s.Rva + s.Size) is { Size: > 0 } s ? (int)(rva - s.Rva + s.Raw) : null;

        IEnumerable<(uint Id, bool Named, uint Target)> Entries(int dir)
        {
            int named = U16(dir + 12), ids = U16(dir + 14);
            for (int i = 0; i < named + ids; i++)
                yield return (U32(dir + 16 + 8 * i), i < named, U32(dir + 20 + 8 * i));
        }

        int? Child(int dir, Func<(uint Id, bool Named, uint Target), bool> pick) =>
            Entries(dir).Where(pick).Select(e => (int?)e.Target).FirstOrDefault() is { } t ? root + (t & 0x7FFF_FFFF) : null;

        byte[]? Data(int languages)
        {
            if (Child(languages, _ => true) is not { } leaf || Offset(U32(leaf)) is not { } at) return null;
            int size = (int)U32(leaf + 4);
            if (size < 0 || at < 0 || at + (long)size > length) throw new ArgumentOutOfRangeException(nameof(size));   // before the allocation
            var data = new byte[size];
            Read(at, data);
            return data;
        }

        public byte[]? First(uint type) =>
            Child(root, e => !e.Named && e.Id == type) is { } names && Child(names, _ => true) is { } languages ? Data(languages) : null;

        public byte[]? Find(uint type, int id) =>
            Child(root, e => !e.Named && e.Id == type) is { } names && Child(names, e => !e.Named && e.Id == id) is { } languages ? Data(languages) : null;
    }
}
