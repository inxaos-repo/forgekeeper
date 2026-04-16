using Forgekeeper.Core.Enums;

namespace Forgekeeper.Core.Models;

public class Model3D
{
    public Guid Id { get; set; }
    public Guid CreatorId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? SourceId { get; set; }
    public SourceType Source { get; set; }
    public Guid? SourceEntityId { get; set; }
    public string? SourceUrl { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public string? Scale { get; set; }
    public string? GameSystem { get; set; }
    public int FileCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public string? ThumbnailPath { get; set; }
    public List<string> PreviewImages { get; set; } = [];
    public string BasePath { get; set; } = string.Empty;
    public int? Rating { get; set; }
    public string? Notes { get; set; }

    /// <summary>
    /// JSONB column for source-specific extra data from metadata.json
    /// </summary>
    public string? Extra { get; set; }

    /// <summary>
    /// Print history entries stored as JSONB. Each entry records a print attempt.
    /// </summary>
    public List<PrintHistoryEntry>? PrintHistory { get; set; }

    /// <summary>
    /// Component list stored as JSONB. Defines the parts that make up this model.
    /// </summary>
    public List<ComponentInfo>? Components { get; set; }

    /// <summary>
    /// Denormalized from metadata License.Type for search/filter.
    /// </summary>
    public string? LicenseType { get; set; }

    /// <summary>
    /// Denormalized from metadata Collection.Name for grouping.
    /// </summary>
    public string? CollectionName { get; set; }

    /// <summary>
    /// Computed from PrintHistory — true if any print was successful.
    /// Not stored in database; computed on read.
    /// </summary>
    public bool Printed => PrintHistory?.Any(p => p.Result == "success") ?? false;

    public DateTime? ExternalCreatedAt { get; set; }
    public DateTime? ExternalUpdatedAt { get; set; }
    public DateTime? DownloadedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastScannedAt { get; set; }

    // Navigation
    public Creator Creator { get; set; } = null!;
    public Source? SourceEntity { get; set; }
    public List<Variant> Variants { get; set; } = [];
    public List<Tag> Tags { get; set; } = [];
}
