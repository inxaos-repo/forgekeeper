namespace Forgekeeper.Core.Models;

/// <summary>
/// Represents a configured source directory (e.g., mmf, thangs, patreon).
/// Each source maps to a directory under sources/{slug}/ and has an adapter type.
/// </summary>
public class Source
{
    public Guid Id { get; set; }

    /// <summary>
    /// URL-safe slug used as the directory name under sources/ (e.g., "mmf", "thangs", "patreon")
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display name (e.g., "MyMiniFactory", "Thangs")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Filesystem path to the source directory (e.g., "/mnt/3dprinting/sources/mmf")
    /// </summary>
    public string BasePath { get; set; } = string.Empty;

    /// <summary>
    /// Adapter type name for resolving the ISourceAdapter implementation
    /// (e.g., "MmfSourceAdapter", "GenericSourceAdapter", "PatreonSourceAdapter")
    /// </summary>
    public string AdapterType { get; set; } = "GenericSourceAdapter";

    /// <summary>
    /// Whether this source is automatically scanned on periodic/incremental scans
    /// </summary>
    public bool AutoScan { get; set; } = true;

    /// <summary>#91: "platform" (creator storefront), "channel" (delivery-only, e.g. Telegram), "manual". Null = legacy row.</summary>
    public string? Kind { get; set; }

    /// <summary>#91: homepage of the platform, if any.</summary>
    public string? WebsiteUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public List<Model3D> Models { get; set; } = [];
}
