namespace Forgekeeper.PluginSdk;

/// <summary>
/// Summary of a model found during manifest fetch.
/// Contains enough info to decide whether to scrape it and to display in the UI.
/// </summary>
public class ScrapedModel
{
    /// <summary>External ID on the source platform.</summary>
    public required string ExternalId { get; init; }

    /// <summary>Model name/title.</summary>
    public required string Name { get; init; }

    /// <summary>Creator's display name.</summary>
    public string? CreatorName { get; init; }

    /// <summary>Creator's external ID on the source platform.</summary>
    public string? CreatorId { get; init; }

    /// <summary>Last updated timestamp from the source.</summary>
    public DateTime? UpdatedAt { get; init; }

    /// <summary>Model type/category (e.g., "miniature", "terrain", "bust").</summary>
    public string? Type { get; init; }

    /// <summary>Additional source-specific data.</summary>
    public Dictionary<string, object>? Extra { get; init; }

    // ── SDK 1.1 additions (all optional; older plugins/hosts ignore them) ──

    /// <summary>Creator's username/slug on the source platform (SDK 1.1).</summary>
    public string? CreatorUsername { get; init; }

    /// <summary>
    /// Every way the user acquired this item (purchase, subscription, campaign …).
    /// A single library item can appear in several manifest rows; after dedupe all
    /// rows' acquisitions are merged here (SDK 1.1).
    /// </summary>
    public IReadOnlyList<ScrapedAcquisition> Acquisitions { get; init; } = [];

    /// <summary>Tags carried on the manifest row(s) (SDK 1.1).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>When the item first entered the user's library (SDK 1.1).</summary>
    public DateTime? LibraryAddedAt { get; init; }

    /// <summary>
    /// For a bundle child: the prefixed id of the parent bundle (e.g. "bundle-3147").
    /// Null for standalone items and for the bundle row itself (SDK 1.1).
    /// </summary>
    public string? BundleId { get; init; }
}

/// <summary>One acquisition record for a library item (SDK 1.1).</summary>
public class ScrapedAcquisition
{
    /// <summary>Raw source-specific acquisition kind (e.g. MMF "PURCHASE", "TRIBE").</summary>
    public required string Source { get; init; }

    /// <summary>Normalized method name matching Forgekeeper's AcquisitionMethod enum.</summary>
    public string? Method { get; init; }

    /// <summary>When this acquisition happened, if known.</summary>
    public DateTime? AcquiredAt { get; init; }

    /// <summary>Source-specific reference (order id, campaign/tribe name …).</summary>
    public string? Reference { get; init; }
}
