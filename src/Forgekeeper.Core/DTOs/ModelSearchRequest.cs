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
    public string? SortBy { get; set; } = "name";
    public bool SortDescending { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}
