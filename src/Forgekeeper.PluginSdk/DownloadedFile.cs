namespace Forgekeeper.PluginSdk;

/// <summary>
/// Represents a file downloaded by a plugin during model scraping.
/// </summary>
public class DownloadedFile
{
    /// <summary>Filename (not full path).</summary>
    public required string Filename { get; init; }

    /// <summary>Full local path where the file was saved.</summary>
    public required string LocalPath { get; init; }

    /// <summary>File size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Variant classification (e.g., "supported", "unsupported", "presupported").</summary>
    public string? Variant { get; init; }

    /// <summary>Whether this file is an archive (ZIP, RAR, etc.) that should be extracted.</summary>
    public bool IsArchive { get; init; }
}
