using Forgekeeper.Core.Enums;

namespace Forgekeeper.Core.Models;

public class Creator
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public SourceType Source { get; set; }
    public string? SourceUrl { get; set; }
    public string? ExternalId { get; set; }
    public string? AvatarUrl { get; set; }
    public int ModelCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// A Creator row is a per-source identity ("Print Minis" on MMF, "Print Minis"
    /// on Patreon). When set, this row is an alias of the canonical creator.
    /// Null means this row IS canonical.
    /// </summary>
    public Guid? CanonicalCreatorId { get; set; }

    // Navigation
    public Creator? CanonicalCreator { get; set; }
    public List<Creator> Aliases { get; set; } = [];
    public List<Model3D> Models { get; set; } = [];
}
