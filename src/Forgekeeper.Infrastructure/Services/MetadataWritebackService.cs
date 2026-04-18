using System.Text.Json;
using System.Text.Json.Serialization;
using Forgekeeper.Core.Models;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// Writes user-owned fields (print history, rating, notes, tags, components, print settings)
/// back to metadata.json on disk. This ensures the metadata.json file is always in sync with
/// the database and enables database-free recovery.
/// </summary>
public class MetadataWritebackService
{
    private readonly ILogger<MetadataWritebackService> _logger;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public MetadataWritebackService(ILogger<MetadataWritebackService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Write user-owned fields from the model back to its metadata.json file.
    /// Preserves all existing fields — only overwrites user-owned ones.
    /// </summary>
    public async Task WritebackAsync(Model3D model, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(model.BasePath))
        {
            _logger.LogDebug("No BasePath for model {Id} — skipping writeback", model.Id);
            return;
        }

        var metadataPath = Path.Combine(model.BasePath, "metadata.json");

        try
        {
            // Read existing metadata (preserve source-owned fields)
            Dictionary<string, object?>? existing = null;
            if (File.Exists(metadataPath))
            {
                var json = await File.ReadAllTextAsync(metadataPath, ct);
                existing = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, ReadOptions);
            }

            existing ??= new Dictionary<string, object?>();

            // Write user-owned fields
            if (model.Rating.HasValue)
                existing["userRating"] = model.Rating.Value;

            if (!string.IsNullOrEmpty(model.Notes))
                existing["notes"] = model.Notes;

            if (!string.IsNullOrEmpty(model.Category))
                existing["category"] = model.Category;

            if (!string.IsNullOrEmpty(model.GameSystem))
                existing["gameSystem"] = model.GameSystem;

            if (!string.IsNullOrEmpty(model.Scale))
                existing["scale"] = model.Scale;

            if (!string.IsNullOrEmpty(model.PrintStatus))
                existing["printStatus"] = model.PrintStatus;

            if (!string.IsNullOrEmpty(model.CollectionName))
                existing["collection"] = model.CollectionName;

            if (!string.IsNullOrEmpty(model.LicenseType))
                existing["license"] = new { type = model.LicenseType };

            if (model.PrintHistory is { Count: > 0 })
                existing["printHistory"] = model.PrintHistory;

            if (model.Components is { Count: > 0 })
                existing["components"] = model.Components;

            if (model.PrintSettings != null)
                existing["printSettings"] = model.PrintSettings;

            if (model.Tags.Count > 0)
                existing["userTags"] = model.Tags
                    .Where(t => t.Source == "user" || t.Source == null)
                    .Select(t => t.Name)
                    .ToList();

            existing["lastWriteback"] = DateTime.UtcNow.ToString("o");

            var output = JsonSerializer.Serialize(existing, WriteOptions);
            await File.WriteAllTextAsync(metadataPath, output, ct);

            _logger.LogDebug("Wrote metadata.json for model {Id} at {Path}", model.Id, metadataPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write metadata.json for model {Id} at {Path}", model.Id, metadataPath);
            // Non-fatal — DB is the source of truth
        }
    }
}
