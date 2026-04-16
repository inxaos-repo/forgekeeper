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

    public static ScrapeResult Failure(string error) => new() { Success = false, Error = error };

    public static ScrapeResult Ok(string metadataFile, IReadOnlyList<DownloadedFile> files) =>
        new() { Success = true, MetadataFile = metadataFile, Files = files };
}
