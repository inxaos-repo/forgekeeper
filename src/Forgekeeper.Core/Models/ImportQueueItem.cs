using Forgekeeper.Core.Enums;

namespace Forgekeeper.Core.Models;

public class ImportQueueItem
{
    public Guid Id { get; set; }
    public string OriginalPath { get; set; } = string.Empty;
    public string? DetectedCreator { get; set; }
    public string? DetectedModelName { get; set; }
    public SourceType? DetectedSource { get; set; }
    public VariantType? DetectedVariantType { get; set; }
    public double ConfidenceScore { get; set; }
    public ImportStatus Status { get; set; } = ImportStatus.Pending;

    // User-confirmed values
    public string? ConfirmedCreator { get; set; }
    public string? ConfirmedModelName { get; set; }
    public SourceType? ConfirmedSource { get; set; }

    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
