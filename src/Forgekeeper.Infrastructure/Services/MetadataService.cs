using System.Text.Json;
using System.Text.Json.Serialization;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Core.Models;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// Reads, writes, and merges metadata.json sidecar files.
/// Respects the field ownership matrix from the implementation plan.
/// </summary>
public class MetadataService : IMetadataService
{
    private readonly ILogger<MetadataService> _logger;

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

    public MetadataService(ILogger<MetadataService> logger)
    {
        _logger = logger;
    }

    public async Task<SourceMetadata?> ReadAsync(string modelDirectory, CancellationToken ct = default)
    {
        var path = Path.Combine(modelDirectory, "metadata.json");
        if (!File.Exists(path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<SourceMetadata>(json, ReadOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse metadata.json at {Path}", path);
            return null;
        }
    }

    public async Task WriteAsync(string modelDirectory, SourceMetadata metadata, CancellationToken ct = default)
    {
        var path = Path.Combine(modelDirectory, "metadata.json");
        var json = JsonSerializer.Serialize(metadata, WriteOptions);
        await File.WriteAllTextAsync(path, json, ct);
        _logger.LogDebug("Wrote metadata.json to {Path}", path);
    }

    public async Task MergeAsync(string modelDirectory, Model3D model, CancellationToken ct = default)
    {
        var path = Path.Combine(modelDirectory, "metadata.json");
        if (!File.Exists(path))
        {
            // No existing file — backfill instead
            await BackfillAsync(modelDirectory, model, ct);
            return;
        }

        try
        {
            // Read existing as a raw JSON document to preserve scraper-owned fields
            var json = await File.ReadAllTextAsync(path, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Deserialize to modify Forgekeeper-owned fields only
            var metadata = JsonSerializer.Deserialize<SourceMetadata>(json, ReadOptions);
            if (metadata == null) return;

            // Tags: union merge — add model tags that aren't already present
            var existingTags = metadata.Tags ?? [];
            var modelTags = model.Tags.Select(t => t.Name).ToList();
            var merged = existingTags.Union(modelTags, StringComparer.OrdinalIgnoreCase).ToList();
            metadata.Tags = merged;

            // NOTE: PrintHistory, Rating, Notes, and other user-owned fields are written back
            // to metadata.json by MetadataWritebackService.WritebackAsync — not here.
            // MergeAsync only handles tag union-merge and structural fields (Components, PrintSettings).

            if (model.Components is { Count: > 0 })
                metadata.Components = model.Components;

            if (model.PrintSettings != null)
                metadata.PrintSettings = model.PrintSettings;

            // Write back
            var updatedJson = JsonSerializer.Serialize(metadata, WriteOptions);
            await File.WriteAllTextAsync(path, updatedJson, ct);
            _logger.LogDebug("Merged Forgekeeper-owned fields into metadata.json at {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to merge metadata.json at {Path}", path);
        }
    }

    public async Task BackfillAsync(string modelDirectory, Model3D model, CancellationToken ct = default)
    {
        var path = Path.Combine(modelDirectory, "metadata.json");
        if (File.Exists(path))
            return; // Don't overwrite existing files

        var metadata = new SourceMetadata
        {
            MetadataVersion = 1,
            Source = model.Source.ToString().ToLowerInvariant(),
            ExternalId = model.SourceId ?? model.Id.ToString(),
            Name = model.Name,
            Creator = new MetadataCreator
            {
                DisplayName = model.Creator?.Name,
            },
            Dates = new MetadataDates
            {
                Downloaded = model.DownloadedAt ?? model.CreatedAt,
                Created = model.ExternalCreatedAt,
                Updated = model.ExternalUpdatedAt,
            },
            Tags = model.Tags.Select(t => t.Name).ToList(),
            License = !string.IsNullOrEmpty(model.LicenseType)
                ? new LicenseInfo { Type = model.LicenseType }
                : null,
            Collection = !string.IsNullOrEmpty(model.CollectionName)
                ? new CollectionInfo { Name = model.CollectionName }
                : null,
            Components = model.Components,
            PrintSettings = model.PrintSettings,
            Files = EnumerateModelFiles(modelDirectory),
        };

        await WriteAsync(modelDirectory, metadata, ct);
        _logger.LogInformation("Backfilled metadata.json at {Path}", path);
    }

    private static List<MetadataFile> EnumerateModelFiles(string modelDirectory)
    {
        if (!Directory.Exists(modelDirectory))
            return [];

        return Directory.EnumerateFiles(modelDirectory, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("metadata.json", StringComparison.OrdinalIgnoreCase))
            .Select(f =>
            {
                var relPath = Path.GetRelativePath(modelDirectory, f);
                return new MetadataFile
                {
                    Filename = Path.GetFileName(f),
                    OriginalFilename = Path.GetFileName(f),
                    LocalPath = relPath,
                    Size = new FileInfo(f).Length,
                    Variant = DetectVariantFromPath(relPath),
                };
            })
            .ToList();
    }

    private static string? DetectVariantFromPath(string relativePath)
    {
        var pathLower = relativePath.Replace('\\', '/').ToLowerInvariant();
        var segments = pathLower.Split('/');

        foreach (var segment in segments)
        {
            if (segment is "supported" or "sup") return "supported";
            if (segment is "unsupported" or "unsup" or "nosup") return "unsupported";
            if (segment is "presupported" or "pre-supported" or "presup") return "presupported";
            if (segment is "lychee") return "lychee";
        }

        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        return ext switch
        {
            ".lys" => "lychee",
            ".ctb" or ".cbddlp" => "chitubox",
            ".gcode" => "gcode",
            _ => "unsupported"
        };
    }
}
