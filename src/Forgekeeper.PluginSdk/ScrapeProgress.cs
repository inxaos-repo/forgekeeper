namespace Forgekeeper.PluginSdk;

/// <summary>
/// Progress update reported during scraping operations.
/// </summary>
public class ScrapeProgress
{
    /// <summary>Current status (e.g., "authenticating", "fetching_manifest", "downloading", "complete", "error").</summary>
    public required string Status { get; init; }

    /// <summary>Current item index (0-based).</summary>
    public int Current { get; init; }

    /// <summary>Total number of items (0 if unknown).</summary>
    public int Total { get; init; }

    /// <summary>Name/description of the item currently being processed.</summary>
    public string? CurrentItem { get; init; }
}
