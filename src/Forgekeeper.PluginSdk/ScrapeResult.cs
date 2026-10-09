namespace Forgekeeper.PluginSdk;

/// <summary>
/// Result of scraping a single model. Contains the metadata file path and list of downloaded files.
/// </summary>
public class ScrapeResult
{
    /// <summary>Whether the scrape completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>
    /// Path to the metadata.json file written by the plugin (relative to the model directory).
    /// Null on failure.
    /// </summary>
    public string? MetadataFile { get; init; }

    /// <summary>List of files downloaded for this model.</summary>
    public IReadOnlyList<DownloadedFile> Files { get; init; } = [];

    /// <summary>Error message if the scrape failed.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// SDK 1.1: the source rejected our credentials (expired/revoked token). The host
    /// must stop the sync, checkpoint at this item, and ask the user to re-authenticate
    /// rather than continuing (every following item would fail the same way).
    /// </summary>
    public bool AuthExpired { get; init; }

    /// <summary>
    /// SDK 1.1: the item was deliberately not downloaded (e.g. the source exposes no
    /// downloadable files for it). Counted as skipped, not failed. <see cref="Error"/> holds the reason.
    /// </summary>
    public bool Skipped { get; init; }

    public static ScrapeResult Skip(string reason) => new() { Success = false, Skipped = true, Error = reason };

    public static ScrapeResult TokenExpired(string error) =>
        new() { Success = false, AuthExpired = true, Error = error };

    public static ScrapeResult Failure(string error) => new() { Success = false, Error = error };

    public static ScrapeResult Ok(string metadataFile, IReadOnlyList<DownloadedFile> files) =>
        new() { Success = true, MetadataFile = metadataFile, Files = files };
}
