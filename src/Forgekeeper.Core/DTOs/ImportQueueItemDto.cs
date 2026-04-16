using Forgekeeper.Core.Enums;

namespace Forgekeeper.Core.DTOs;

public class ImportQueueItemDto
{
    public Guid Id { get; set; }
    public string OriginalPath { get; set; } = string.Empty;
    public string? DetectedCreator { get; set; }
    public string? DetectedModelName { get; set; }
    public SourceType? DetectedSource { get; set; }
    public double ConfidenceScore { get; set; }
    public ImportStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ImportConfirmRequest
{
    public string Creator { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public SourceType Source { get; set; }
    /// <summary>
    /// Source slug (e.g., "mmf", "thangs") — preferred over SourceType enum.
    /// If provided, overrides Source enum for determining the target directory.
    /// </summary>
    public string? SourceSlug { get; set; }
}
