using Forgekeeper.Core.Models;

namespace Forgekeeper.Core.Interfaces;

/// <summary>
/// Service for reading, writing, and merging metadata.json sidecar files.
/// Respects the ownership matrix: Forgekeeper never overwrites scraper-owned fields.
/// </summary>
public interface IMetadataService
{
    /// <summary>
    /// Read and deserialize a metadata.json from a model directory.
    /// Returns null if file doesn't exist or is malformed.
    /// </summary>
    Task<SourceMetadata?> ReadAsync(string modelDirectory, CancellationToken ct = default);

    /// <summary>
    /// Write a metadata.json file for manually imported items.
    /// Only used when no metadata.json exists (backfill or manual import).
    /// </summary>
    Task WriteAsync(string modelDirectory, SourceMetadata metadata, CancellationToken ct = default);

    /// <summary>
    /// Merge Forgekeeper-owned fields back into an existing metadata.json.
    /// Preserves scraper-owned fields; only updates: tags (union), printHistory,
    /// components, printSettings, relatedModels, physicalProperties.
    /// </summary>
    Task MergeAsync(string modelDirectory, Model3D model, CancellationToken ct = default);

    /// <summary>
    /// Create a backfill metadata.json from filesystem scan data when the file is missing.
    /// Uses directory name parsing and file enumeration to populate basic fields.
    /// </summary>
    Task BackfillAsync(string modelDirectory, Model3D model, CancellationToken ct = default);
}
