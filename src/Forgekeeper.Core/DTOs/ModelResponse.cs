using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Models;

namespace Forgekeeper.Core.DTOs;

public class ModelResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CreatorName { get; set; } = string.Empty;
    public Guid CreatorId { get; set; }
    public SourceType Source { get; set; }
    public string? SourceSlug { get; set; }
    public string? SourceId { get; set; }
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
    public bool Printed { get; set; }
    public int? Rating { get; set; }
    public string? Notes { get; set; }
    public string? LicenseType { get; set; }
    public string? CollectionName { get; set; }
    public List<string> Tags { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ModelDetailResponse : ModelResponse
{
    public List<VariantResponse> Variants { get; set; } = [];
    public List<PrintHistoryEntry>? PrintHistory { get; set; }
    public List<ComponentInfo>? Components { get; set; }
}

public class VariantResponse
{
    public Guid Id { get; set; }
    public VariantType VariantType { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public FileType FileType { get; set; }
    public long FileSizeBytes { get; set; }
    public string? ThumbnailPath { get; set; }
    public PhysicalProperties? PhysicalProperties { get; set; }
}

public class ModelUpdateRequest
{
    public string? Name { get; set; }
    public string? Category { get; set; }
    public string? Scale { get; set; }
    public string? GameSystem { get; set; }
    public bool? Printed { get; set; }
    public int? Rating { get; set; }
    public string? Notes { get; set; }
}

public class AddPrintRequest
{
    public string Date { get; set; } = "";
    public string? Printer { get; set; }
    public string? Technology { get; set; }
    public string? Material { get; set; }
    public double? LayerHeight { get; set; }
    public double? Scale { get; set; }
    public string Result { get; set; } = "success";
    public string? Notes { get; set; }
    public string? Duration { get; set; }
    public List<string>? Photos { get; set; }
    public string? Variant { get; set; }
}

public class UpdateComponentsRequest
{
    public List<ComponentInfo> Components { get; set; } = [];
}
