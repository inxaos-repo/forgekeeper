using System.Text.Json;
using System.Text.Json.Serialization;
using Forgekeeper.Core.DTOs;
using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Infrastructure.Services;

public class ImportService : IImportService
{
    private readonly ForgeDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<ImportService> _logger;

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z"
    };

    private static readonly HashSet<string> ModelExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".stl", ".obj", ".3mf", ".lys", ".ctb", ".cbddlp", ".gcode", ".sl1"
    };

    public ImportService(ForgeDbContext db, IConfiguration config, ILogger<ImportService> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    public async Task<List<ImportQueueItemDto>> ProcessUnsortedAsync(CancellationToken ct = default)
    {
        var basePaths = _config.GetSection("Storage:BasePaths").Get<string[]>() ?? ["/mnt/3dprinting"];
        var results = new List<ImportQueueItemDto>();

        foreach (var basePath in basePaths)
        {
            var unsortedDir = Path.Combine(basePath, "unsorted");
            if (!Directory.Exists(unsortedDir))
                continue;

            // Process archives first
            foreach (var file in Directory.EnumerateFiles(unsortedDir, "*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(file);

                if (ArchiveExtensions.Contains(ext))
                {
                    var extractedDir = await ExtractArchiveAsync(file, ct);
                    if (extractedDir != null)
                    {
                        _logger.LogInformation("Extracted {Archive} to {Dir}", file, extractedDir);
                    }
                }
            }

            // Now process all entries in unsorted/
            foreach (var entry in Directory.EnumerateFileSystemEntries(unsortedDir))
            {
                ct.ThrowIfCancellationRequested();

                // Skip already-queued items
                var existing = await _db.ImportQueue
                    .FirstOrDefaultAsync(q => q.OriginalPath == entry, ct);
                if (existing != null)
                    continue;

                var queueItem = AnalyzeEntry(entry, basePath);
                _db.ImportQueue.Add(queueItem);
                await _db.SaveChangesAsync(ct);

                results.Add(MapToDto(queueItem));
            }
        }

        return results;
    }

    public async Task<List<ImportQueueItemDto>> GetQueueAsync(ImportStatus? status = null, CancellationToken ct = default)
    {
        var query = _db.ImportQueue.AsQueryable();
        if (status.HasValue)
            query = query.Where(q => q.Status == status.Value);

        return await query
            .OrderByDescending(q => q.CreatedAt)
            .Select(q => MapToDto(q))
            .ToListAsync(ct);
    }

    public async Task ConfirmImportAsync(Guid queueItemId, ImportConfirmRequest request, CancellationToken ct = default)
    {
        var item = await _db.ImportQueue.FindAsync([queueItemId], ct)
            ?? throw new KeyNotFoundException($"Import queue item {queueItemId} not found");

        item.ConfirmedCreator = request.Creator;
        item.ConfirmedModelName = request.ModelName;
        item.ConfirmedSource = request.Source;
        item.Status = ImportStatus.Confirmed;
        item.UpdatedAt = DateTime.UtcNow;

        // Move files to canonical location
        try
        {
            var basePaths = _config.GetSection("Storage:BasePaths").Get<string[]>() ?? ["/mnt/3dprinting"];
            var basePath = basePaths[0];
            var sourceSlug = !string.IsNullOrEmpty(request.SourceSlug)
                ? request.SourceSlug
                : request.Source.ToString().ToLowerInvariant();
            var targetDir = Path.Combine(basePath, "sources", sourceSlug, request.Creator, request.ModelName);

            Directory.CreateDirectory(targetDir);

            if (Directory.Exists(item.OriginalPath))
            {
                foreach (var file in Directory.EnumerateFiles(item.OriginalPath, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(item.OriginalPath, file);
                    var destPath = Path.Combine(targetDir, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    File.Move(file, destPath, overwrite: false);
                }
                Directory.Delete(item.OriginalPath, recursive: true);
            }
            else if (File.Exists(item.OriginalPath))
            {
                var destPath = Path.Combine(targetDir, Path.GetFileName(item.OriginalPath));
                File.Move(item.OriginalPath, destPath, overwrite: false);
            }

            // Write metadata.json for manually imported items (if one doesn't already exist)
            var metadataPath = Path.Combine(targetDir, "metadata.json");
            if (!File.Exists(metadataPath))
            {
                var metadata = new SourceMetadata
                {
                    MetadataVersion = 1,
                    Source = sourceSlug,
                    ExternalId = Guid.NewGuid().ToString(),
                    Name = request.ModelName,
                    Creator = new MetadataCreator
                    {
                        DisplayName = request.Creator,
                    },
                    Dates = new MetadataDates
                    {
                        Downloaded = DateTime.UtcNow,
                    },
                    License = new LicenseInfo { Type = "unknown" },
                    Files = Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories)
                        .Where(f => !Path.GetFileName(f).Equals("metadata.json", StringComparison.OrdinalIgnoreCase))
                        .Select(f =>
                        {
                            var relPath = Path.GetRelativePath(targetDir, f);
                            return new MetadataFile
                            {
                                Filename = Path.GetFileName(f),
                                OriginalFilename = Path.GetFileName(f),
                                LocalPath = relPath,
                                Size = new FileInfo(f).Length,
                                Variant = DetectVariantFromPath(relPath),
                            };
                        })
                        .ToList(),
                };

                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                };
                await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, jsonOptions));
                _logger.LogInformation("Wrote metadata.json to {Path}", metadataPath);
            }

            _logger.LogInformation("Imported {Path} to {Target}", item.OriginalPath, targetDir);
        }
        catch (Exception ex)
        {
            item.Status = ImportStatus.Failed;
            item.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Failed to import {Path}", item.OriginalPath);
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task DismissAsync(Guid queueItemId, CancellationToken ct = default)
    {
        var item = await _db.ImportQueue.FindAsync([queueItemId], ct)
            ?? throw new KeyNotFoundException($"Import queue item {queueItemId} not found");

        _db.ImportQueue.Remove(item);
        await _db.SaveChangesAsync(ct);
    }

    private ImportQueueItem AnalyzeEntry(string path, string basePath)
    {
        var item = new ImportQueueItem
        {
            Id = Guid.NewGuid(),
            OriginalPath = path,
            Status = ImportStatus.Pending,
        };

        var name = Path.GetFileName(path);
        var isDirectory = Directory.Exists(path);

        // Try to detect creator by matching against known creators
        var knownCreators = _db.Creators.Select(c => c.Name).ToList();
        foreach (var creator in knownCreators)
        {
            if (name.Contains(creator, StringComparison.OrdinalIgnoreCase))
            {
                item.DetectedCreator = creator;
                item.ConfidenceScore += 0.4;
                break;
            }
        }

        // If directory, check for variant subfolders (indicates 3D model collection)
        if (isDirectory)
        {
            var subdirs = Directory.GetDirectories(path).Select(Path.GetFileName).ToList();
            var hasVariants = subdirs.Any(d =>
                d != null && (d.Equals("supported", StringComparison.OrdinalIgnoreCase) ||
                             d.Equals("unsupported", StringComparison.OrdinalIgnoreCase) ||
                             d.Equals("presupported", StringComparison.OrdinalIgnoreCase)));

            if (hasVariants)
            {
                item.ConfidenceScore += 0.3;
                item.DetectedModelName = name;
            }

            // Check for metadata.json
            if (File.Exists(Path.Combine(path, "metadata.json")))
            {
                item.ConfidenceScore += 0.3;
            }
        }
        else
        {
            // Single file
            var ext = Path.GetExtension(name);
            if (ModelExtensions.Contains(ext))
            {
                item.DetectedModelName = Path.GetFileNameWithoutExtension(name);
                item.ConfidenceScore += 0.2;
            }
        }

        item.Status = item.ConfidenceScore >= 0.7
            ? ImportStatus.AutoSorted
            : ImportStatus.AwaitingReview;

        return item;
    }

    private async Task<string?> ExtractArchiveAsync(string archivePath, CancellationToken ct)
    {
        var ext = Path.GetExtension(archivePath).ToLowerInvariant();
        if (ext != ".zip") return null; // Only ZIP for MVP

        var extractDir = Path.Combine(
            Path.GetDirectoryName(archivePath)!,
            Path.GetFileNameWithoutExtension(archivePath));

        if (Directory.Exists(extractDir)) return extractDir;

        try
        {
            Directory.CreateDirectory(extractDir);
            using var zipFile = new ZipFile(archivePath);

            foreach (ZipEntry entry in zipFile)
            {
                ct.ThrowIfCancellationRequested();
                if (!entry.IsFile) continue;

                var entryPath = Path.Combine(extractDir, entry.Name);
                var entryDir = Path.GetDirectoryName(entryPath);
                if (entryDir != null) Directory.CreateDirectory(entryDir);

                using var zipStream = zipFile.GetInputStream(entry);
                using var outStream = File.Create(entryPath);
                await zipStream.CopyToAsync(outStream, ct);
            }

            // Delete the archive after successful extraction
            File.Delete(archivePath);
            return extractDir;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract {Archive}", archivePath);
            return null;
        }
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

    private static ImportQueueItemDto MapToDto(ImportQueueItem item) => new()
    {
        Id = item.Id,
        OriginalPath = item.OriginalPath,
        DetectedCreator = item.DetectedCreator,
        DetectedModelName = item.DetectedModelName,
        DetectedSource = item.DetectedSource,
        ConfidenceScore = item.ConfidenceScore,
        Status = item.Status,
        CreatedAt = item.CreatedAt,
    };
}
