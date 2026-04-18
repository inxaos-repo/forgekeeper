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
    public AcquisitionMethod? AcquisitionMethod { get; set; }
    public string? AcquisitionOrderId { get; set; }
    public DateTime? PublishedAt { get; set; }
    public List<string> Tags { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ModelDetailResponse : ModelResponse
{
    public List<VariantResponse> Variants { get; set; } = [];
    public List<PrintHistoryEntry>? PrintHistory { get; set; }
    public List<ComponentInfo>? Components { get; set; }
    public PrintSettingsInfo? PrintSettings { get; set; }
    public List<RelatedModelSummary> RelatedModels { get; set; } = [];
}

/// <summary>
/// Lightweight summary of a related model for API responses.
/// </summary>
public class RelatedModelSummary
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ThumbnailPath { get; set; }
    public string RelationType { get; set; } = string.Empty;
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

/// <summary>
/// Request to bulk-update multiple models at once.
/// </summary>
public class BulkUpdateRequest
{
    /// <summary>
    /// List of model IDs to update.
    /// </summary>
    public List<Guid> ModelIds { get; set; } = [];

    /// <summary>
    /// Operation to perform: tag, categorize, setGameSystem, setScale, setRating, setLicense.
    /// </summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>
    /// Value for the operation (tag name, category name, etc.).
    /// </summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Response for bulk operations showing what was affected.
/// </summary>
public class BulkUpdateResponse
{
    public int AffectedCount { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Request to add/remove tags from multiple models at once.
/// </summary>
public class BulkTagsRequest
{
    public List<Guid> ModelIds { get; set; } = [];
    public List<string> AddTags { get; set; } = [];
    public List<string> RemoveTags { get; set; } = [];
}

public class BulkTagsResponse
{
    public int AffectedCount { get; set; }
    public int TagsAdded { get; set; }
    public int TagsRemoved { get; set; }
}

/// <summary>
/// Request to add a relation between two models.
/// </summary>
public class AddRelationRequest
{
    public Guid RelatedModelId { get; set; }
    public string RelationType { get; set; } = "collection";
}

/// <summary>
/// Summary of a potential duplicate pair.
/// </summary>
public class DuplicateGroup
{
    public List<DuplicateModel> Models { get; set; } = [];
    public string MatchType { get; set; } = string.Empty; // name, hash
    public double Similarity { get; set; }
}

public class DuplicateModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CreatorName { get; set; } = string.Empty;
    public string? BasePath { get; set; }
    public long TotalSizeBytes { get; set; }
}

/// <summary>
/// Request for the bulk-metadata endpoint — apply multiple field updates and tag changes in one call.
/// </summary>
public class BulkMetadataRequest
{
    /// <summary>Model IDs to update (max 500).</summary>
    public List<Guid> ModelIds { get; set; } = [];

    /// <summary>
    /// Fields to set. Null values are ignored (not cleared). Supported keys:
    /// creator, category, scale, gameSystem, licenseType, collectionName, printStatus, rating, notes.
    /// </summary>
    public Dictionary<string, string?> Fields { get; set; } = [];

    /// <summary>Tags to add to every model in the set.</summary>
    public List<string> AddTags { get; set; } = [];

    /// <summary>Tags to remove from every model in the set.</summary>
    public List<string> RemoveTags { get; set; } = [];
}

public class BulkMetadataResponse
{
    public int AffectedCount { get; set; }
    public int TagsAdded { get; set; }
    public int TagsRemoved { get; set; }
    public List<string> Errors { get; set; } = [];
}

/// <summary>Request body for POST /api/v1/import/scan.</summary>
public class ImportScanRequest
{
    /// <summary>Absolute path to scan on the server filesystem.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Whether to recurse into subdirectories (default true).</summary>
    public bool Recursive { get; set; } = true;

    /// <summary>Maximum depth of recursion (0 = unlimited).</summary>
    public int MaxDepth { get; set; } = 0;
}

/// <summary>Top-level result from a scan operation.</summary>
public class ImportScanResult
{
    public string ScannedPath { get; set; } = string.Empty;
    public int TotalDirectoriesScanned { get; set; }
    public int DetectedModels { get; set; }
    public int AlreadyInLibrary { get; set; }
    public List<DetectedModelEntry> Models { get; set; } = [];
    public DateTime ScannedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A single potential model detected during a scan.</summary>
public class DetectedModelEntry
{
    public string FolderPath { get; set; } = string.Empty;
    public string DetectedModelName { get; set; } = string.Empty;
    public string? DetectedCreatorName { get; set; }
    public bool AlreadyInLibrary { get; set; }
    public Guid? ExistingModelId { get; set; }
    public List<DetectedVariantFile> Files { get; set; } = [];
    public int TotalFiles { get; set; }
    public long TotalSizeBytes { get; set; }
    public bool HasMetadataJson { get; set; }
}

/// <summary>A single file inside a detected model folder.</summary>
public class DetectedVariantFile
{
    public string RelativePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string? DetectedVariant { get; set; }
}

/// <summary>
/// Request to rename a single model's directory or reassign it to a different creator.
/// </summary>
public class RenameRequest
{
    /// <summary>New name for the model (renames the directory on disk).</summary>
    public string? NewName { get; set; }

    /// <summary>Reassign to a different creator (moves directory into creator folder).</summary>
    public string? NewCreator { get; set; }
}

/// <summary>
/// Request to preview what a set of models would look like after a template rename.
/// Does not move any files.
/// </summary>
public class RenamePreviewRequest
{
    public List<Guid> ModelIds { get; set; } = [];

    /// <summary>Naming template, e.g. "{Creator CleanName}/{Model CleanName}"</summary>
    public string? Template { get; set; }
}

/// <summary>
/// Request to bulk-reassign models to a different creator, optionally moving files on disk.
/// </summary>
public class BulkCreatorRequest
{
    public List<Guid> ModelIds { get; set; } = [];

    /// <summary>Name of the creator to reassign to (created if not found).</summary>
    public string CreatorName { get; set; } = string.Empty;

    /// <summary>If true, move model directories on disk into the creator's folder.</summary>
    public bool MoveFiles { get; set; } = true;
}

/// <summary>Response for bulk-creator endpoint.</summary>
public class BulkCreatorResponse
{
    public int AffectedCount { get; set; }
    public string CreatorName { get; set; } = string.Empty;
    public Guid CreatorId { get; set; }
    public int FilesMovedCount { get; set; }
    public List<string> Errors { get; set; } = [];
}

/// <summary>Input for PreviewRename in NamingTemplateService.</summary>
public class ModelRenameInput
{
    public Guid ModelId { get; set; }
    public string CurrentPath { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string CreatorName { get; set; } = string.Empty;
    public string? Variant { get; set; }
    public string? Scale { get; set; }
    public string? Source { get; set; }
    public string? Category { get; set; }
    public string? GameSystem { get; set; }
    public string? FileType { get; set; }
    public DateTime? DateAdded { get; set; }
    public string? Collection { get; set; }
    public List<string> Files { get; set; } = [];
}

/// <summary>Preview of what a rename would do.</summary>
public class RenamePreview
{
    public Guid ModelId { get; set; }
    public string CurrentPath { get; set; } = string.Empty;
    public string NewPath { get; set; } = string.Empty;
    public List<FileRenamePreview> Files { get; set; } = [];
}

public class FileRenamePreview
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
}
