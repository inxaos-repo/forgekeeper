using Forgekeeper.Core.Enums;

namespace Forgekeeper.Core.Models;

public class Variant
{
    public Guid Id { get; set; }
    public Guid ModelId { get; set; }
    public VariantType VariantType { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public FileType FileType { get; set; }
    public long FileSizeBytes { get; set; }
    public string? ThumbnailPath { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Physical properties (bounding box, triangle count, etc.) stored as JSONB.
    /// Populated by external tools or future mesh analysis; not computed automatically.
    /// </summary>
    public PhysicalProperties? PhysicalProperties { get; set; }

    // Navigation
    public Model3D Model { get; set; } = null!;
}
