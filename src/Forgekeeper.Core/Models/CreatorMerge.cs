namespace Forgekeeper.Core.Models;

/// <summary>
/// Multi-source attribution phase 2 (#92): a reversible creator merge. The merged creator row is kept
/// (with <see cref="Creator.MergedIntoId"/> set); <see cref="Snapshot"/> records exactly which rows moved
/// so <c>unmerge</c> can put them back.
/// </summary>
public class CreatorMerge
{
    public Guid Id { get; set; }
    public Guid SurvivorId { get; set; }
    public Guid MergedId { get; set; }

    /// <summary>JSON: { modelIds:[], profileIds:[], aliasIds:[], createdAliasId }.</summary>
    public string Snapshot { get; set; } = "{}";
    public string? Reason { get; set; }
    public string? MergedBy { get; set; }
    public DateTime MergedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UnmergedAt { get; set; }
}
