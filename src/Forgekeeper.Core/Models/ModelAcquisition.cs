using Forgekeeper.Core.Enums;

namespace Forgekeeper.Core.Models;

/// <summary>Multi-source attribution phase 3 (#93): one row per way a model was obtained.</summary>
public class ModelAcquisition
{
    public Guid Id { get; set; }
    public Guid ModelId { get; set; }
    public AcquisitionKind Kind { get; set; } = AcquisitionKind.Origin;
    public Guid? SourceId { get; set; }

    /// <summary>Source slug, denormalised (e.g. "mmf", "telegram").</summary>
    public string? SourceSlug { get; set; }
    public string? ExternalId { get; set; }
    public string? ExternalUrl { get; set; }
    public DateTime? AcquiredAt { get; set; }
    public string? License { get; set; }
    public string? Tier { get; set; }
    public string? OrderRef { get; set; }
    public string? PledgeRef { get; set; }

    /// <summary>JSONB: which files of the model this acquisition covers (null = all).</summary>
    public string? FileSelector { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Model3D? Model { get; set; }
    public Source? Source { get; set; }
}
