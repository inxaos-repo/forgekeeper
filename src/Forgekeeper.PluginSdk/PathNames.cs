namespace Forgekeeper.PluginSdk;

/// <summary>
/// The one folder/file-name sanitizer shared by the host and plugins (#104).
/// The host downloader, the importer, the API and the MMF plugin each had their own copy; folders
/// written by the legacy MiniDownloader used '-' where these use '_' (or kept ':'), so a re-sync
/// created an empty sibling ("A_B") next to the real folder ("A-B"). Build library path segments
/// with <see cref="Sanitize"/> and match existing folders with <see cref="ComparisonKey"/>.
/// </summary>
public static class PathNames
{
    /// <summary>
    /// Sanitize one path segment: OS-invalid filename characters become '_', then trim.
    /// Output is byte-for-byte what the host and plugin produced before, so existing folders keep matching.
    /// Empty / whitespace input → <paramref name="fallback"/>.
    /// </summary>
    public static string Sanitize(string? name, string fallback = "unknown")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length == 0 || s is "." or ".." ? fallback : s;
    }

    /// <summary>
    /// Separator-insensitive key for matching folder names written by different sanitizers:
    /// "A 1:270 B", "A 1-270 B" and "A 1_270 B" all give the same key (letters/digits only, lower-cased).
    /// </summary>
    public static string ComparisonKey(string name) =>
        new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// Find an existing sibling folder under <paramref name="parentDir"/> whose name has the same
    /// <see cref="ComparisonKey"/> as <paramref name="folderName"/>. Exact name wins; otherwise a folder
    /// with content (per <paramref name="hasContent"/>) beats an empty one. Null if none.
    /// </summary>
    public static string? FindEquivalentDirectory(string parentDir, string folderName, Func<string, bool>? hasContent = null)
    {
        var exact = Path.Combine(parentDir, folderName);
        if (Directory.Exists(exact) && (hasContent == null || hasContent(exact))) return exact;
        if (!Directory.Exists(parentDir)) return null;
        var key = ComparisonKey(folderName);
        if (key.Length == 0) return Directory.Exists(exact) ? exact : null;
        List<string> matches;
        try
        {
            matches = Directory.EnumerateDirectories(parentDir)
                .Where(d => !Path.GetFileName(d).StartsWith('.') && ComparisonKey(Path.GetFileName(d)) == key)
                .OrderBy(d => d, StringComparer.Ordinal).ToList();
        }
        catch { return Directory.Exists(exact) ? exact : null; }
        if (hasContent != null)
        {
            var withContent = matches.FirstOrDefault(hasContent);
            if (withContent != null) return withContent;
        }
        if (Directory.Exists(exact)) return exact;
        return matches.FirstOrDefault();
    }
}
