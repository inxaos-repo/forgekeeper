using System.Runtime.InteropServices;
using System.Text;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// #41 / #58: Linux filenames are raw bytes. A name that isn't valid UTF-8 (e.g. a Latin-1
/// 0xE9 'é' or 0xB0 '°' from an old CP437/Latin-1 zip) is enumerated by .NET with U+FFFD in
/// place of the bad byte, and that lossy string can't be used to reopen the file
/// (FileNotFoundException). This helper reads directory entries as raw bytes through libc
/// and can rename such entries to a valid UTF-8 name (invalid bytes decoded as Windows-1252,
/// falling back to Latin-1), so the file becomes indexable.
/// </summary>
public static class RawFileNames
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>True when the .NET-enumerated name lost bytes (contains U+FFFD).</summary>
    public static bool LooksLossy(string name) => name.Contains('\uFFFD');

    public static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try { StrictUtf8.GetCharCount(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    /// <summary>
    /// Decode a raw filename: valid UTF-8 runs stay as-is, every byte that is not part of a
    /// valid UTF-8 sequence is mapped through Windows-1252 (Latin-1 for its undefined slots).
    /// Control characters and '/' never result; unmapped C1 bytes become '_'.
    /// </summary>
    public static string RepairName(ReadOnlySpan<byte> raw)
    {
        var sb = new StringBuilder(raw.Length);
        int i = 0;
        while (i < raw.Length)
        {
            int len = Utf8SequenceLength(raw[i..]);
            if (len > 0)
            {
                sb.Append(Encoding.UTF8.GetString(raw.Slice(i, len)));
                i += len;
            }
            else
            {
                sb.Append(MapLegacyByte(raw[i]));
                i++;
            }
        }
        return sb.ToString();
    }

    private static int Utf8SequenceLength(ReadOnlySpan<byte> s)
    {
        byte b = s[0];
        int len = b < 0x80 ? 1 : (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : (b & 0xF8) == 0xF0 ? 4 : 0;
        if (len == 0 || s.Length < len) return 0;
        return IsValidUtf8(s[..len]) ? len : 0;
    }

    // Windows-1252 0x80-0x9F; everything else in 0xA0-0xFF is identical to Latin-1.
    private static readonly char[] Cp1252High =
    {
        '€', '_', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '_', 'Ž', '_',
        '_', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '_', 'ž', 'Ÿ',
    };

    private static char MapLegacyByte(byte b) =>
        b >= 0x80 && b <= 0x9F ? Cp1252High[b - 0x80] : b < 0x20 || b == 0x7F ? '_' : (char)b;

    /// <summary>Raw (byte) names of the entries in <paramref name="dir"/>, excluding . and ..</summary>
    public static List<byte[]> ListRaw(string dir)
    {
        var result = new List<byte[]>();
        if (!OperatingSystem.IsLinux()) return result;
        var d = opendir(Encoding.UTF8.GetBytes(dir + "\0"));
        if (d == IntPtr.Zero) return result;
        try
        {
            IntPtr ent;
            while ((ent = readdir(d)) != IntPtr.Zero)
            {
                // struct dirent (glibc/musl x64 & arm64): d_ino(8) d_off(8) d_reclen(2) d_type(1) d_name[]
                var namePtr = ent + 19;
                int n = 0;
                while (Marshal.ReadByte(namePtr, n) != 0) n++;
                var bytes = new byte[n];
                Marshal.Copy(namePtr, bytes, 0, n);
                if (n == 1 && bytes[0] == '.' || n == 2 && bytes[0] == '.' && bytes[1] == '.') continue;
                result.Add(bytes);
            }
        }
        finally { closedir(d); }
        return result;
    }

    /// <summary>
    /// Rename every non-UTF-8 entry directly inside <paramref name="dir"/> (and, when
    /// recursive, in subdirectories) to its repaired name. Never overwrites: if the target
    /// exists, a " (n)" suffix is added before the extension. Returns (old, new) pairs.
    /// </summary>
    public static List<(string OldDisplay, string NewPath)> RepairDirectory(string dir, bool recursive, bool dryRun = false)
    {
        var renamed = new List<(string, string)>();
        if (!OperatingSystem.IsLinux() || !Directory.Exists(dir)) return renamed;
        var dirBytes = Encoding.UTF8.GetBytes(dir.TrimEnd('/'));

        foreach (var raw in ListRaw(dir))
        {
            string current;
            if (!IsValidUtf8(raw))
            {
                var fixedName = RepairName(raw);
                var target = UniqueTarget(Path.Combine(dir, fixedName));
                if (!dryRun)
                {
                    var src = new byte[dirBytes.Length + 1 + raw.Length + 1];
                    dirBytes.CopyTo(src, 0);
                    src[dirBytes.Length] = (byte)'/';
                    raw.CopyTo(src, dirBytes.Length + 1);
                    if (rename(src, Encoding.UTF8.GetBytes(target + "\0")) != 0)
                        throw new IOException($"rename failed for '{Path.Combine(dir, Encoding.UTF8.GetString(raw))}' (errno {Marshal.GetLastPInvokeError()})");
                }
                renamed.Add((Path.Combine(dir, Encoding.UTF8.GetString(raw)), target));
                current = dryRun ? "" : target;
            }
            else current = Path.Combine(dir, Encoding.UTF8.GetString(raw));

            if (recursive && current.Length > 0 && Directory.Exists(current)
                && !new FileInfo(current).Attributes.HasFlag(FileAttributes.ReparsePoint))
                renamed.AddRange(RepairDirectory(current, true, dryRun));
        }
        return renamed;
    }

    private static string UniqueTarget(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int n = 1; ; n++)
        {
            var p = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    [DllImport("libc", SetLastError = true)] private static extern IntPtr opendir(byte[] name);
    [DllImport("libc", SetLastError = true)] private static extern IntPtr readdir(IntPtr dir);
    [DllImport("libc")] private static extern int closedir(IntPtr dir);
    [DllImport("libc", SetLastError = true)] private static extern int rename(byte[] oldPath, byte[] newPath);
}
