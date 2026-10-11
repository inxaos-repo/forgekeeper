using Microsoft.Extensions.Logging;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// #75/#77: handling for model folders left behind by interrupted downloads. A process kill (OOM,
/// pod restart) mid-download can leave a folder that holds only a hidden <c>.name.zip.&lt;guid&gt;.part</c>
/// file and/or a stub <c>metadata.json</c>, or nothing at all. Such a folder is not a model.
/// </summary>
public static class PartialDownloadCleanup
{
    private static readonly HashSet<string> NonContentFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "metadata.json", ".ds_store", "thumbs.db", ".tmp",
    };

    /// <summary>True for an in-progress / orphaned download temp file.</summary>
    public static bool IsPartFile(string fileName) =>
        fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="dir"/> contains no real content: only metadata.json, hidden files,
    /// <c>.part</c> temp files, OS junk, or nothing. Hidden subfolders are ignored too.
    /// </summary>
    public static bool IsStubOrPartialDir(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, f);
                if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s.StartsWith('.')))
                    continue; // hidden file or inside a hidden folder (.part temp files are hidden)
                var name = Path.GetFileName(f);
                if (NonContentFiles.Contains(name) || IsPartFile(name)) continue;
                return false;
            }
            return true;
        }
        catch { return false; } // unreadable: don't make claims about it
    }

    /// <summary>
    /// Move orphaned <c>.part</c> files (older than <paramref name="minAge"/>) found directly in
    /// creator/model folders of <paramref name="sourceDir"/> into
    /// <c>sourceDir/.trash-yyyyMMdd/&lt;creator&gt;/&lt;model&gt;/</c>. Never deletes. Only call while no
    /// download for this source is in flight (e.g. at sync start, holding the sync lock).
    /// </summary>
    public static int SweepOrphanParts(string sourceDir, TimeSpan minAge, ILogger? logger = null, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var trashRoot = Path.Combine(sourceDir, $".trash-{now:yyyyMMdd}");
        int moved = 0;
        foreach (var creator in SafeDirs(sourceDir))
        {
            foreach (var model in SafeDirs(creator))
            {
                string[] parts;
                try { parts = Directory.GetFiles(model, "*.part"); } catch { continue; }
                foreach (var p in parts)
                {
                    try
                    {
                        if (now - File.GetLastWriteTimeUtc(p) < minAge) continue;
                        var dest = Path.Combine(trashRoot, Path.GetFileName(creator), Path.GetFileName(model));
                        Directory.CreateDirectory(dest);
                        var target = Path.Combine(dest, Path.GetFileName(p));
                        if (File.Exists(target)) target += "." + Guid.NewGuid().ToString("N")[..8];
                        File.Move(p, target);
                        moved++;
                        logger?.LogWarning("Moved orphaned partial download {Part} to {Target}", p, target);
                    }
                    catch (Exception ex) { logger?.LogWarning(ex, "Could not move orphaned partial download {Part}", p); }
                }
            }
        }
        return moved;
    }

    private static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.GetDirectories(path).Where(d => !Path.GetFileName(d).StartsWith('.')).ToArray(); }
        catch { return []; }
    }
}
