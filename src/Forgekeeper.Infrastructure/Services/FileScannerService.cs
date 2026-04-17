using System.Text.Json;
using Forgekeeper.Core.DTOs;
using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Infrastructure.Services;

public class FileScannerService : IScannerService
{
    private readonly IDbContextFactory<ForgeDbContext> _dbFactory;
    private readonly IEnumerable<ISourceAdapter> _adapters;
    private readonly IMetadataService _metadataService;
    private readonly IConfiguration _config;
    private readonly ILogger<FileScannerService> _logger;
    private ScanProgress _progress = new();
    private readonly object _lock = new();

    private static readonly HashSet<string> ModelFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".stl", ".obj", ".3mf", ".lys", ".ctb", ".cbddlp", ".gcode", ".sl1"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif"
    };

    private static readonly HashSet<string> IgnoreFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ds_store", "thumbs.db", ".tmp"
    };

    public FileScannerService(
        IDbContextFactory<ForgeDbContext> dbFactory,
        IEnumerable<ISourceAdapter> adapters,
        IMetadataService metadataService,
        IConfiguration config,
        ILogger<FileScannerService> logger)
    {
        _dbFactory = dbFactory;
        _adapters = adapters;
        _metadataService = metadataService;
        _config = config;
        _logger = logger;
    }

    public bool IsRunning
    {
        get { lock (_lock) return _progress.IsRunning; }
    }

    public ScanProgress GetProgress()
    {
        lock (_lock) return _progress;
    }

    public async Task<ScanProgress> ScanAsync(bool incremental = false, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_progress.IsRunning)
                return _progress;

            _progress = new ScanProgress
            {
                IsRunning = true,
                Status = "starting",
                StartedAt = DateTime.UtcNow
            };
        }

        try
        {
            var basePaths = _config.GetSection("Storage:BasePaths").Get<string[]>() ?? ["/mnt/3dprinting"];

            foreach (var basePath in basePaths)
            {
                var sourcesDir = Path.Combine(basePath, "sources");
                if (!Directory.Exists(sourcesDir))
                {
                    _logger.LogWarning("Sources directory not found: {Path}", sourcesDir);
                    continue;
                }

                foreach (var sourceDir in Directory.GetDirectories(sourcesDir))
                {
                    ct.ThrowIfCancellationRequested();
                    await ScanSourceDirectoryAsync(sourceDir, incremental, ct);
                }
            }

            lock (_lock)
            {
                _progress.IsRunning = false;
                _progress.Status = "completed";
                _progress.CompletedAt = DateTime.UtcNow;
                _progress.ElapsedSeconds = (DateTime.UtcNow - _progress.StartedAt!.Value).TotalSeconds;
            }
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                _progress.IsRunning = false;
                _progress.Status = "cancelled";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan failed");
            lock (_lock)
            {
                _progress.IsRunning = false;
                _progress.Status = "failed";
                _progress.Error = ex.Message;
            }
        }

        return GetProgress();
    }

    private async Task ScanSourceDirectoryAsync(string sourceDir, bool incremental, CancellationToken ct)
    {
        var sourceName = Path.GetFileName(sourceDir);
        _logger.LogInformation("Scanning source: {Source}", sourceName);

        // Ensure Source entity exists in DB for this source directory
        await using var sourceDb = await _dbFactory.CreateDbContextAsync(ct);
        var sourceEntity = await sourceDb.Sources.FirstOrDefaultAsync(s => s.Slug == sourceName, ct);
        if (sourceEntity == null)
        {
            sourceEntity = new Source
            {
                Id = Guid.NewGuid(),
                Slug = sourceName,
                Name = sourceName,
                BasePath = sourceDir,
                AdapterType = "GenericSourceAdapter",
                AutoScan = true,
            };
            sourceDb.Sources.Add(sourceEntity);
            await sourceDb.SaveChangesAsync(ct);
        }

        UpdateStatus($"scanning {sourceName}");

        // Walk creator directories
        foreach (var creatorDir in SafeGetDirectories(sourceDir))
        {
            ct.ThrowIfCancellationRequested();

            // Walk model directories under creator
            foreach (var modelDir in SafeGetDirectories(creatorDir))
            {
                ct.ThrowIfCancellationRequested();

                // For Patreon, check one more level (release/model)
                if (sourceName.Equals("patreon", StringComparison.OrdinalIgnoreCase))
                {
                    // Check if this is a release dir containing model subdirs
                    var subDirs = SafeGetDirectories(modelDir);
                    if (subDirs.Length > 0 && subDirs.Any(d => HasModelFiles(d)))
                    {
                        foreach (var subModelDir in subDirs)
                        {
                            try { await ProcessModelDirectoryAsync(subModelDir, incremental, ct); }
                            catch (Exception ex) { _logger.LogError(ex, "Failed to process model: {Path}", subModelDir); }
                        }
                        continue;
                    }
                }

                try { await ProcessModelDirectoryAsync(modelDir, incremental, ct); }
                catch (Exception ex) { _logger.LogError(ex, "Failed to process model: {Path}", modelDir); }
            }
        }

        // Reconcile: flag models whose files are missing from disk
        await ReconcileSourceAsync(sourceDir, ct);
    }

    private async Task ProcessModelDirectoryAsync(string modelDir, bool incremental, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Check if already scanned (incremental mode)
        if (incremental)
        {
            var scanState = await db.ScanStates
                .FirstOrDefaultAsync(s => s.DirectoryPath == modelDir, ct);

            if (scanState != null)
            {
                var lastWrite = Directory.GetLastWriteTimeUtc(modelDir);
                if (lastWrite <= scanState.LastModifiedAt)
                    return; // No changes
            }
        }

        // Find the right adapter
        var adapter = _adapters.FirstOrDefault(a => a.CanHandle(modelDir));
        if (adapter == null)
        {
            _logger.LogDebug("No adapter for: {Path}", modelDir);
            return;
        }

        var parsed = adapter.ParseModelDirectory(modelDir);
        if (parsed == null) return;

        lock (_lock) _progress.DirectoriesScanned++;

        // Check for metadata.json via MetadataService
        var metadata = await _metadataService.ReadAsync(modelDir, ct);

        // Get or create creator
        var creatorName = SanitizeMetadataValue(
            metadata?.Creator?.DisplayName
            ?? metadata?.Creator?.Username
            ?? parsed.CreatorName);

        var creator = await db.Creators.FirstOrDefaultAsync(
            c => c.Name == creatorName && c.Source == parsed.Source, ct);

        if (creator == null)
        {
            creator = new Creator
            {
                Id = Guid.NewGuid(),
                Name = creatorName,
                Source = parsed.Source,
                SourceUrl = metadata?.Creator?.ProfileUrl,
                ExternalId = metadata?.Creator?.ExternalId,
                AvatarUrl = metadata?.Creator?.AvatarUrl,
            };
            db.Creators.Add(creator);
            await db.SaveChangesAsync(ct);
        }

        // Get or create model
        var model = await db.Models
            .Include(m => m.Variants)
            .Include(m => m.Tags)
            .FirstOrDefaultAsync(m => m.BasePath == modelDir, ct);

        var isNew = model == null;
        if (model == null)
        {
            model = new Model3D
            {
                Id = Guid.NewGuid(),
                CreatorId = creator.Id,
                BasePath = modelDir,
                Source = parsed.Source,
            };

            // Link to Source entity by adapter's slug
            var linkedSource = await db.Sources.FirstOrDefaultAsync(s => s.Slug == parsed.SourceSlug, ct);
            if (linkedSource != null)
                model.SourceEntityId = linkedSource.Id;
            db.Models.Add(model);
        }

        // Update model from metadata or parsed info
        model.Name = SanitizeMetadataValue(metadata?.Name ?? parsed.ModelName);
        model.SourceId = metadata?.ExternalId ?? parsed.SourceId;
        model.SourceUrl = metadata?.ExternalUrl;
        model.Description = metadata?.Description;
        model.Extra = metadata?.Extra != null ? JsonSerializer.Serialize(metadata.Extra) : null;
        model.ExternalCreatedAt = metadata?.Dates?.Created;
        model.ExternalUpdatedAt = metadata?.Dates?.Updated;
        model.DownloadedAt = metadata?.Dates?.Downloaded;
        model.LastScannedAt = DateTime.UtcNow;
        model.UpdatedAt = DateTime.UtcNow;

        // Denormalized fields from metadata
        model.LicenseType = metadata?.License?.Type;
        model.CollectionName = metadata?.Collection?.Name;
        model.PublishedAt = metadata?.Dates?.Published;
        model.PrintSettings = metadata?.PrintSettings;

        // Acquisition fields from metadata
        if (metadata?.Acquisition != null)
        {
            if (Enum.TryParse<AcquisitionMethod>(metadata.Acquisition.Method, true, out var method))
                model.AcquisitionMethod = method;
            model.AcquisitionOrderId = metadata.Acquisition.OrderId;
        }

        // Import components from metadata (only if not already set by user)
        if (metadata?.Components != null && (model.Components == null || model.Components.Count == 0))
            model.Components = metadata.Components;

        // Scan files and variants
        var existingVariantPaths = model.Variants.Select(v => v.FilePath).ToHashSet();
        var allFiles = ScanModelFiles(modelDir);
        var previewImages = new List<string>();

        foreach (var (filePath, relativePath, fileName) in allFiles)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();

            if (ImageExtensions.Contains(ext))
            {
                previewImages.Add(relativePath);
                continue;
            }

            if (!ModelFileExtensions.Contains(ext))
                continue;

            if (existingVariantPaths.Contains(relativePath))
                continue;

            var variant = new Variant
            {
                Id = Guid.NewGuid(),
                ModelId = model.Id,
                VariantType = DetectVariantType(relativePath, ext),
                FilePath = relativePath,
                FileName = fileName,
                FileType = DetectFileType(ext),
                FileSizeBytes = new FileInfo(filePath).Length,
            };
            model.Variants.Add(variant);
        }

        model.PreviewImages = previewImages;
        model.FileCount = model.Variants.Count;
        model.TotalSizeBytes = model.Variants.Sum(v => v.FileSizeBytes);

        // Import tags from metadata
        if (metadata?.Tags != null && model.Tags.Count == 0)
        {
            foreach (var tagName in metadata.Tags.Select(t => t.ToLowerInvariant().Trim()).Distinct())
            {
                // Check DB first, then check locally-tracked entities to avoid duplicate inserts
                var tag = await db.Tags.FirstOrDefaultAsync(t => t.Name == tagName, ct)
                    ?? db.Tags.Local.FirstOrDefault(t => t.Name == tagName);
                if (tag == null)
                {
                    tag = new Tag { Id = Guid.NewGuid(), Name = tagName };
                    db.Tags.Add(tag);
                    await db.SaveChangesAsync(ct); // Flush to avoid duplicate on next model
                }
                if (!model.Tags.Contains(tag))
                    model.Tags.Add(tag);
            }
        }

        // Backfill metadata.json if it doesn't exist (database-free recovery)
        if (metadata == null && Directory.Exists(modelDir))
        {
            try
            {
                await _metadataService.BackfillAsync(modelDir, model, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to backfill metadata.json at {Path}", modelDir);
            }
        }

        // Update creator model count
        creator.ModelCount = await db.Models.CountAsync(m => m.CreatorId == creator.Id, ct) + (isNew ? 1 : 0);

        // Update scan state
        var state = await db.ScanStates.FirstOrDefaultAsync(s => s.DirectoryPath == modelDir, ct);
        if (state == null)
        {
            state = new ScanState { Id = Guid.NewGuid(), DirectoryPath = modelDir };
            db.ScanStates.Add(state);
        }
        state.LastScannedAt = DateTime.UtcNow;
        state.LastModifiedAt = Directory.GetLastWriteTimeUtc(modelDir);
        state.FileCount = model.FileCount;

        await db.SaveChangesAsync(ct);

        lock (_lock)
        {
            _progress.ModelsFound++;
            _progress.FilesFound += model.FileCount;
            if (isNew) _progress.NewModels++;
            else _progress.UpdatedModels++;
        }
    }

    private static List<(string FullPath, string RelativePath, string FileName)> ScanModelFiles(string modelDir)
    {
        var results = new List<(string, string, string)>();

        foreach (var file in Directory.EnumerateFiles(modelDir, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);
            if (IgnoreFiles.Contains(fileName))
                continue;

            var relativePath = Path.GetRelativePath(modelDir, file);
            results.Add((file, relativePath, fileName));
        }

        return results;
    }

    public static VariantType DetectVariantType(string relativePath, string extension)
    {
        var pathLower = relativePath.Replace('\\', '/').ToLowerInvariant();
        var segments = pathLower.Split('/');

        // Check folder-based variant detection
        foreach (var segment in segments)
        {
            if (segment is "supported" or "sup")
                return VariantType.Supported;
            if (segment is "unsupported" or "unsup" or "nosup")
                return VariantType.Unsupported;
            if (segment is "presupported" or "pre-supported" or "presup")
                return VariantType.Presupported;
            if (segment is "lychee")
                return VariantType.LycheeProject;
            if (segment is "chitubox")
                return VariantType.ChituboxProject;
            if (segment is "images")
                return VariantType.PreviewImage;
        }

        // Check extension-based detection
        return extension.ToLowerInvariant() switch
        {
            ".lys" => VariantType.LycheeProject,
            ".ctb" or ".cbddlp" => VariantType.ChituboxProject,
            ".gcode" => VariantType.Gcode,
            ".3mf" => VariantType.PrintProject,
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" => VariantType.PreviewImage,
            _ => VariantType.Unsupported // default for STL/OBJ at root
        };
    }

    public static FileType DetectFileType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".stl" => FileType.Stl,
            ".obj" => FileType.Obj,
            ".3mf" => FileType.Threemf,
            ".lys" => FileType.Lys,
            ".ctb" => FileType.Ctb,
            ".cbddlp" => FileType.Cbddlp,
            ".gcode" => FileType.Gcode,
            ".sl1" => FileType.Sl1,
            ".png" => FileType.Png,
            ".jpg" or ".jpeg" => FileType.Jpg,
            ".webp" => FileType.Webp,
            _ => FileType.Other
        };
    }

    private static bool HasModelFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Any(f => ModelFileExtensions.Contains(Path.GetExtension(f)));
        }
        catch { return false; }
    }

    private static string[] SafeGetDirectories(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch { return []; }
    }

    private void UpdateStatus(string status)
    {
        lock (_lock) _progress.Status = status;
    }

    /// <summary>
    /// Sanitize metadata values to prevent directory traversal or invalid path characters
    /// being stored in the database from disk metadata.
    /// </summary>
    private static string SanitizeMetadataValue(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        // Remove directory traversal patterns
        var sanitized = input.Replace("..", "__");
        // Remove control characters
        sanitized = new string(sanitized.Where(c => !char.IsControl(c)).ToArray());
        return sanitized.Trim();
    }

    /// <summary>
    /// Reconcile DB records against filesystem — flag models whose files have all gone missing.
    /// Called after scanning a source directory to detect removed models.
    /// </summary>
    private async Task ReconcileSourceAsync(string sourceDir, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sourceName = Path.GetFileName(sourceDir);

        // Get all models whose BasePath is under this source directory
        var models = await db.Models
            .Include(m => m.Variants)
            .Where(m => m.BasePath.StartsWith(sourceDir))
            .ToListAsync(ct);

        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();

            // Check if the model directory itself still exists
            if (!Directory.Exists(model.BasePath))
            {
                _logger.LogWarning(
                    "Model directory missing for {ModelName} (ID: {ModelId}): {BasePath}",
                    model.Name, model.Id, model.BasePath);
                continue;
            }

            // Check if ALL variant files are gone
            if (model.Variants.Count > 0)
            {
                var allMissing = model.Variants.All(v =>
                {
                    var fullPath = Path.Combine(model.BasePath, v.FilePath);
                    return !File.Exists(fullPath);
                });

                if (allMissing)
                {
                    _logger.LogWarning(
                        "All variant files missing for model {ModelName} (ID: {ModelId}, {Count} variants)",
                        model.Name, model.Id, model.Variants.Count);
                }
            }
        }
    }
}
