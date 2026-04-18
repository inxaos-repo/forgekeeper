namespace Forgekeeper.Core.Models;

/// <summary>
/// Represents a resolved plugin release from a GitHub repository.
/// </summary>
public class PluginRelease
{
    /// <summary>Resolved version string (tag name without leading 'v').</summary>
    public string Version { get; set; } = "";

    /// <summary>Direct download URL for the plugin zip asset.</summary>
    public string DownloadUrl { get; set; } = "";

    /// <summary>URL of the SHA256SUMS file, if present in the release assets.</summary>
    public string? ChecksumUrl { get; set; }

    /// <summary>SHA-256 checksum hex string for the zip asset, parsed from SHA256SUMS.</summary>
    public string? Checksum { get; set; }

    /// <summary>File size in bytes of the zip asset.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>Release publish timestamp from GitHub.</summary>
    public DateTime PublishedAt { get; set; }

    /// <summary>Raw tag name from GitHub (e.g., "v1.0.0").</summary>
    public string TagName { get; set; } = "";
}
