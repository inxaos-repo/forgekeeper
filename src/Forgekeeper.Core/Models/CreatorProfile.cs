namespace Forgekeeper.Core.Models;

/// <summary>
/// Multi-source attribution phase 2 (#92): one creator, many platform profiles.
/// A profile is the creator's identity on a single source (e.g. their MMF store, their Patreon).
/// </summary>
public class CreatorProfile
{
    public Guid Id { get; set; }
    public Guid CreatorId { get; set; }
    public Guid? SourceId { get; set; }

    /// <summary>Source slug (denormalised for convenience/back-compat, e.g. "mmf", "patreon").</summary>
    public string Platform { get; set; } = string.Empty;
    public string? Handle { get; set; }
    public string? Url { get; set; }
    public string? ExternalId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Creator? Creator { get; set; }
    public Source? Source { get; set; }
}
