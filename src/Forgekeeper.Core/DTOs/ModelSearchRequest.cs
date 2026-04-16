using Forgekeeper.Core.Enums;

namespace Forgekeeper.Core.DTOs;

public class ModelSearchRequest
{
    public string? Query { get; set; }
    public Guid? CreatorId { get; set; }
    public string? Category { get; set; }
    public string? GameSystem { get; set; }
    public string? Scale { get; set; }
    public SourceType? Source { get; set; }
    public string? SourceSlug { get; set; }
    public FileType? FileType { get; set; }
    public bool? Printed { get; set; }
    public int? MinRating { get; set; }
    public string? LicenseType { get; set; }
    public string? CollectionName { get; set; }

    /// <summary>
    /// Filter by tag name(s). Comma-separated for multiple tags.
    /// </summary>
    public string? Tags { get; set; }

    /// <summary>
    /// Filter by creator name (substring match).
    /// </summary>
    public string? Creator { get; set; }

    /// <summary>
    /// Filter by acquisition method (Purchase, Subscription, Free, Campaign, Gift).
    /// </summary>
    public AcquisitionMethod? AcquisitionMethod { get; set; }

    /// <summary>
    /// Filter models published on or after this date.
    /// </summary>
    public DateTime? PublishedAfter { get; set; }

    /// <summary>
    /// Filter models published on or before this date.
    /// </summary>
    public DateTime? PublishedBefore { get; set; }

    public string? SortBy { get; set; } = "name";
    public bool SortDescending { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}
