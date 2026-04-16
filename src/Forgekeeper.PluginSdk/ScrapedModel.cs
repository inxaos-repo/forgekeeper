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
}
