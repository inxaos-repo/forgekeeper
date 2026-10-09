using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Scraper.Mmf;

/// <summary>Limits applied to every archive extraction (zip-bomb / zip-slip guards).</summary>
public sealed record ArchiveLimits
{
    /// <summary>Max total uncompressed bytes for one archive (default 64 GiB).</summary>
    public long MaxTotalBytes { get; init; } = 64L * 1024 * 1024 * 1024;
    /// <summary>Max number of entries in one archive.</summary>
    public int MaxEntries { get; init; } = 200_000;
    /// <summary>Max compression ratio for an entry (only checked when the entry is larger than <see cref="RatioCheckMinBytes"/>).</summary>
    public double MaxRatio { get; init; } = 1000;
    public long RatioCheckMinBytes { get; init; } = 100L * 1024 * 1024;
    /// <summary>Total nesting depth: 1 = only the outer archive, 2 = outer + one inner level.</summary>
    public int MaxDepth { get; init; } = 2;

    public static ArchiveLimits Default { get; } = new();
}

public sealed record ArchiveExtractResult(bool Success, int Extracted, IReadOnlyList<string> Errors)
{
    public static ArchiveExtractResult Fail(string error) => new(false, 0, [error]);
}

/// <summary>
/// Extracts .zip natively and .rar/.7z via the 7z CLI (p7zip-full), then extracts nested
/// archives one level deep, each into a sibling folder named after the inner archive.
/// Archives are deleted only after their own extraction succeeded; failures keep the file.
/// </summary>
public static class MmfArchiveExtractor
{
    internal static readonly string[] Extensions = [".zip", ".rar", ".7z"];

    /// <summary>Test seam / override for the 7z executable path.</summary>
    internal static string? SevenZipPathOverride { get; set; }

    public static bool IsExtractable(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static string? FindSevenZip()
    {
        if (SevenZipPathOverride != null) return SevenZipPathOverride;
        foreach (var name in new[] { "7z", "7zz", "7za" })
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    /// <summary>True when <paramref name="candidate"/> resolves inside <paramref name="root"/>.</summary>
    internal static bool IsInside(string root, string candidate)
    {
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var c = Path.GetFullPath(candidate);
        return c.StartsWith(r, StringComparison.Ordinal);
    }

    /// <summary>
    /// Extract <paramref name="archivePath"/> into <paramref name="extractDir"/>; then nested archives
    /// (up to <see cref="ArchiveLimits.MaxDepth"/>). When <paramref name="deleteOnSuccess"/> is set,
    /// the outer archive is deleted after a successful extraction; inner archives always are, once
    /// their own extraction succeeded.
    /// </summary>
    public static async Task<ArchiveExtractResult> ExtractAsync(
        string archivePath, string extractDir, ILogger logger, bool deleteOnSuccess,
        ArchiveLimits? limits = null, CancellationToken ct = default)
    {
        limits ??= ArchiveLimits.Default;
        var errors = new List<string>();
        var name = Path.GetFileName(archivePath);
        var (ok, err) = await ExtractOneAsync(archivePath, extractDir, limits, ct);
        if (!ok)
        {
            logger.LogWarning("[UNZIP] Extraction failed, keeping archive {File}: {Error}", name, err);
            return ArchiveExtractResult.Fail(err!);
        }
        logger.LogInformation("[MMF] Extracted: {File}", name);
        int extracted = 1;

        if (limits.MaxDepth >= 2)
        {
            var inner = Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories)
                .Where(IsExtractable).OrderBy(p => p, StringComparer.Ordinal).ToList();
            foreach (var innerPath in inner)
            {
                ct.ThrowIfCancellationRequested();
                var innerDir = InnerExtractDir(innerPath);
                var (iok, ierr) = await ExtractOneAsync(innerPath, innerDir, limits, ct);
                if (!iok)
                {
                    errors.Add($"{Path.GetFileName(innerPath)}: {ierr}");
                    logger.LogWarning("[UNZIP] Nested extraction failed, keeping {File}: {Error}", innerPath, ierr);
                    continue;
                }
                extracted++;
                logger.LogInformation("[MMF] Extracted nested archive: {File}", Path.GetFileName(innerPath));
                TryDelete(innerPath, logger);
            }
        }

        if (deleteOnSuccess) TryDelete(archivePath, logger);
        return new ArchiveExtractResult(true, extracted, errors);
    }

    /// <summary>Folder for a nested archive: sibling directory named after the archive (unique if taken by a file).</summary>
    internal static string InnerExtractDir(string innerArchivePath)
    {
        var dir = Path.Combine(Path.GetDirectoryName(innerArchivePath)!, Path.GetFileNameWithoutExtension(innerArchivePath));
        if (File.Exists(dir)) dir += "_extracted";
        return dir;
    }

    private static void TryDelete(string path, ILogger logger)
    {
        try { File.Delete(path); }
        catch (Exception ex) { logger.LogWarning("[UNZIP] Could not delete archive {File}: {Error}", path, ex.Message); }
    }

    private static async Task<(bool Ok, string? Error)> ExtractOneAsync(
        string archivePath, string extractDir, ArchiveLimits limits, CancellationToken ct)
    {
        try
        {
            var ext = Path.GetExtension(archivePath).ToLowerInvariant();
            return ext == ".zip"
                ? ExtractZip(archivePath, extractDir, limits, ct)
                : await ExtractWith7zAsync(archivePath, extractDir, limits, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, ex.Message); }
    }

    internal static (bool Ok, string? Error) ExtractZip(string archivePath, string extractDir, ArchiveLimits limits, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        if (zip.Entries.Count > limits.MaxEntries)
            return (false, $"too many entries ({zip.Entries.Count} > {limits.MaxEntries})");
        long declared = 0;
        foreach (var e in zip.Entries)
        {
            var target = Path.Combine(extractDir, e.FullName);
            if (Path.IsPathRooted(e.FullName) || !IsInside(extractDir, target))
                return (false, $"unsafe entry path '{e.FullName}' (zip-slip)");
            declared += e.Length;
            if (e.Length > limits.RatioCheckMinBytes && e.CompressedLength > 0 &&
                (double)e.Length / e.CompressedLength > limits.MaxRatio)
                return (false, $"suspicious compression ratio for '{e.FullName}' (zip bomb?)");
        }
        if (declared > limits.MaxTotalBytes)
            return (false, $"uncompressed size {declared} exceeds limit {limits.MaxTotalBytes}");

        Directory.CreateDirectory(extractDir);
        long written = 0;
        var buffer = new byte[81920];
        foreach (var e in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(extractDir, e.FullName));
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var src = e.Open();
            using var dst = File.Create(target);
            int n;
            while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += n;
                if (written > limits.MaxTotalBytes)
                    return (false, "actual uncompressed size exceeds limit (zip bomb?)");
                dst.Write(buffer, 0, n);
            }
            dst.Close();
            try { File.SetLastWriteTime(target, e.LastWriteTime.DateTime); } catch { }
        }
        return (true, null);
    }

    private static async Task<(bool Ok, string? Error)> ExtractWith7zAsync(
        string archivePath, string extractDir, ArchiveLimits limits, CancellationToken ct)
    {
        var sevenZip = FindSevenZip();
        if (sevenZip == null) return (false, "7z not installed (p7zip-full)");

        // Pre-flight: list entries, check paths and sizes before writing anything.
        var (lcode, listing, lerr) = await Run7zAsync(sevenZip, ["l", "-slt", "-ba", "-pforgekeeper-no-password", "--", archivePath], ct);
        if (lcode != 0) return (false, $"7z list failed ({lcode}): {Trim(lerr)}");
        long total = 0; int count = 0;
        foreach (var line in listing.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.StartsWith("Path = ", StringComparison.Ordinal))
            {
                count++;
                var p = l[7..];
                if (p == archivePath) { count--; continue; }
                if (Path.IsPathRooted(p) || !IsInside(extractDir, Path.Combine(extractDir, p)))
                    return (false, $"unsafe entry path '{p}' (path traversal)");
            }
            else if (l.StartsWith("Size = ", StringComparison.Ordinal) && long.TryParse(l[7..], out var s))
                total += s;
        }
        if (count > limits.MaxEntries) return (false, $"too many entries ({count})");
        if (total > limits.MaxTotalBytes) return (false, $"uncompressed size {total} exceeds limit");

        Directory.CreateDirectory(extractDir);
        // dummy password makes encrypted archives fail fast instead of prompting.
        var (code, _, err) = await Run7zAsync(sevenZip, ["x", "-y", "-pforgekeeper-no-password", $"-o{extractDir}", "--", archivePath], ct);
        if (code != 0) return (false, $"7z exit {code}: {Trim(err)}");

        // Post-check: nothing (e.g. via symlinks) escaped the target directory.
        foreach (var f in Directory.EnumerateFileSystemEntries(extractDir, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(f);
            if (info.LinkTarget != null)
            {
                var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(f)!, info.LinkTarget));
                if (!IsInside(extractDir, resolved))
                {
                    try { File.Delete(f); } catch { }
                    return (false, $"archive contained a symlink escaping the target ('{info.Name}')");
                }
            }
        }
        return (true, null);
    }

    private static string Trim(string s) { s = s.Trim(); return s.Length > 400 ? s[..400] : s; }

    private static async Task<(int Code, string Out, string Err)> Run7zAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start 7z");
        proc.StandardInput.Close();
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, await outTask, await errTask);
    }
}
