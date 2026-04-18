using System.Text.Json;
using Forgekeeper.Core.DTOs;
using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

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
            var scannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Scan filesystem-discovered sources (basePath/sources/*/)
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
                    scannedPaths.Add(Path.GetFullPath(sourceDir));
                    await ScanSourceDirectoryAsync(sourceDir, incremental, ct);
                }
            }

            // 2. Scan database-defined sources (added via API/UI) that weren't already scanned
            await using var scanDb = await _dbFactory.CreateDbContextAsync(ct);
            var dbSources = await scanDb.Sources.Where(s => s.AutoScan).ToListAsync(ct);
            foreach (var source in dbSources)
            {
                ct.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(source.BasePath);
                if (scannedPaths.Contains(fullPath))
                    continue; // Already scanned from filesystem discovery

                if (!Directory.Exists(source.BasePath))
                {
                    _logger.LogWarning("Source directory not found for '{Source}': {Path}", source.Name, source.BasePath);
                    continue;
                }

                _logger.LogInformation("Scanning DB-defined source: {Source} at {Path}", source.Name, source.BasePath);
                await ScanSourceDirectoryAsync(source.BasePath, incremental, ct);
            }

            // Update creator model counts with a single GROUP BY — not N separate COUNT queries.
            // N+1 pattern here would fire 5,000+ queries at 5K creators.
            await using var countDb = await _dbFactory.CreateDbContextAsync(ct);
            var counts = await countDb.Models
                .GroupBy(m => m.CreatorId)
                .Select(g => new { CreatorId = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            var countLookup = counts.ToDictionary(x => x.CreatorId, x => x.Count);
            var creators = await countDb.Creators.ToListAsync(ct);
            foreach (var c in creators)
                c.ModelCount = countLookup.GetValueOrDefault(c.Id, 0);
            await countDb.SaveChangesAsync(ct);
            _logger.LogInformation("Updated model counts for {Count} creators", creators.Count);

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

        // One DbContext for the ENTIRE source scan — not one per model.
        // Batch-save every SaveBatchSize models to reduce DB round trips.
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Ensure Source entity exists
        var sourceEntity = await db.Sources.FirstOrDefaultAsync(s => s.Slug == sourceName, ct);
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
            db.Sources.Add(sourceEntity);
            await db.SaveChangesAsync(ct);
        }

        UpdateStatus($"scanning {sourceName}");

        const int SaveBatchSize = 50;
        int pendingChanges = 0;

        async Task FlushBatchAsync()
        {
            if (pendingChanges == 0) return;
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            pendingChanges = 0;
            // Re-attach source entity after clearing the tracker
            sourceEntity = await db.Sources.FirstOrDefaultAsync(s => s.Slug == sourceName, ct) ?? sourceEntity;
        }

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
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                bool changed = await ProcessModelDirectoryAsync(subModelDir, incremental, db, sourceEntity, ct);
                                if (changed) pendingChanges++;
                            }
                            catch (Exception ex) { _logger.LogError(ex, "Failed to process model: {Path}", subModelDir); }

                            if (pendingChanges >= SaveBatchSize)
                                await FlushBatchAsync();
                        }
                        continue;
                    }
                }

                try
                {
                    bool changed = await ProcessModelDirectoryAsync(modelDir, incremental, db, sourceEntity, ct);
                    if (changed) pendingChanges++;
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to process model: {Path}", modelDir); }

                if (pendingChanges >= SaveBatchSize)
                    await FlushBatchAsync();
            }
        }

        // Flush any remaining changes
        await FlushBatchAsync();

        // Reconcile: flag models whose directories are missing from disk
        await ReconcileSourceAsync(sourceDir, db, ct);
    }

    /// <summary>
    /// Process a single model directory. Uses the caller-supplied shared DbContext;
    /// does NOT call SaveChangesAsync — the caller batches saves every SaveBatchSize models.
    /// Returns true if any tracked changes were made.
    /// </summary>
    private async Task<bool> ProcessModelDirectoryAsync(
        string modelDir,
        bool incremental,
        ForgeDbContext db,
        Source? sourceEntity,
        CancellationToken ct)
    {
        // Check if already scanned (incremental mode) — AsNoTracking since we may not continue
        if (incremental)
        {
            var scanState = await db.ScanStates
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.DirectoryPath == modelDir, ct);

            if (scanState != null)
            {
                var lastWrite = Directory.GetLastWriteTimeUtc(modelDir);
                if (lastWrite <= scanState.LastModifiedAt)
                    return false; // No changes
            }
        }

        // Find the right adapter
        var adapter = _adapters.FirstOrDefault(a => a.CanHandle(modelDir));
        if (adapter == null)
        {
            _logger.LogDebug("No adapter for: {Path}", modelDir);
            return false;
        }

        var parsed = adapter.ParseModelDirectory(modelDir);
        if (parsed == null) return false;

        lock (_lock) _progress.DirectoriesScanned++;

        // Check for metadata.json via MetadataService
        var metadata = await _metadataService.ReadAsync(modelDir, ct);

        // Get or create creator
        var creatorName = SanitizeMetadataValue(
            metadata?.Creator?.DisplayName
            ?? metadata?.Creator?.Username
            ?? parsed.CreatorName);

        var creator = await db.Creators.FirstOrDefaultAsync(
            c => c.Name == creatorName && c.Source == parsed.Source, ct)
            // Also check the local cache for creators added in this batch (not yet saved)
            ?? db.Creators.Local.FirstOrDefault(c => c.Name == creatorName && c.Source == parsed.Source);

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
            // No SaveChanges here — EF Core resolves FK ordering automatically on batch save
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

            // Use passed-in source entity (already loaded at source-scan level)
            if (sourceEntity != null)
                model.SourceEntityId = sourceEntity.Id;
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
                // Check DB first, then local cache (unsaved tags from earlier in this batch)
                var tag = await db.Tags.FirstOrDefaultAsync(t => t.Name == tagName, ct)
                    ?? db.Tags.Local.FirstOrDefault(t => t.Name == tagName);
                if (tag == null)
                {
                    tag = new Tag { Id = Guid.NewGuid(), Name = tagName };
                    db.Tags.Add(tag);
                    // No per-tag flush — db.Tags.Local prevents duplicate inserts within this batch
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

        // Note: creator.ModelCount is NOT updated here — the post-scan GROUP BY in ScanAsync
        // handles all creator counts in a single query after all models are processed.

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

        // No SaveChangesAsync here — caller batches saves every SaveBatchSize models.

        lock (_lock)
        {
            _progress.ModelsFound++;
            _progress.FilesFound += model.FileCount;
            if (isNew) _progress.NewModels++;
            else _progress.UpdatedModels++;
        }

        return true; // Changes staged in the shared DbContext
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
    /// Scan source directories and return files/directories that exist on disk
    /// but are not tracked in the database.
    /// </summary>
    public async Task<UntrackedReport> FindUntrackedFilesAsync(string? sourceSlug = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Load all tracked base_path values into a hash set for O(1) lookup
        var trackedPaths = await db.Models
            .Select(m => m.BasePath)
            .ToListAsync(ct);
        var trackedSet = new HashSet<string>(trackedPaths, StringComparer.OrdinalIgnoreCase);

        var report = new UntrackedReport();

        var basePaths = _config.GetSection("Storage:BasePaths").Get<string[]>() ?? ["/mnt/3dprinting"];

        foreach (var basePath in basePaths)
        {
            var sourcesDir = Path.Combine(basePath, "sources");
            if (!Directory.Exists(sourcesDir)) continue;

            foreach (var sourceDir in SafeGetDirectories(sourcesDir))
            {
                var slug = Path.GetFileName(sourceDir);

                // Filter by sourceSlug if provided
                if (!string.IsNullOrEmpty(sourceSlug) &&
                    !slug.Equals(sourceSlug, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Walk creator dirs
                foreach (var creatorDir in SafeGetDirectories(sourceDir))
                {
                    ct.ThrowIfCancellationRequested();

                    // Walk model-level dirs
                    foreach (var modelDir in SafeGetDirectories(creatorDir))
                    {
                        ct.ThrowIfCancellationRequested();

                        if (!trackedSet.Contains(modelDir))
                        {
                            var lastModified = Directory.GetLastWriteTimeUtc(modelDir);
                            report.Items.Add(new UntrackedItem
                            {
                                Path = modelDir,
                                Source = slug,
                                IsDirectory = true,
                                // Skip recursive directory size scan — too expensive on NFS at scale.
                                // Use ?computeSize=true in a future endpoint if needed.
                                SizeBytes = 0,
                                LastModified = lastModified,
                            });
                        }
                    }
                }
            }
        }

        report.TotalOrphans = report.Items.Count;
        report.OrphanSizeBytes = report.Items.Sum(i => i.SizeBytes);
        return report;
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
        catch { return 0L; }
    }

    /// <summary>
    /// Reconcile DB records against filesystem — log models whose directory has gone missing.
    /// Uses the caller's shared DbContext (read-only; no writes, no SaveChanges).
    /// Does NOT load Variants — only checks Directory.Exists on the model path.
    /// </summary>
    private async Task ReconcileSourceAsync(string sourceDir, ForgeDbContext db, CancellationToken ct)
    {
        // Project only the fields we need — don't load Variants at all
        var models = await db.Models
            .Where(m => m.BasePath.StartsWith(sourceDir))
            .AsNoTracking()
            .Select(m => new { m.Id, m.Name, m.BasePath })
            .ToListAsync(ct);

        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();

            if (!Directory.Exists(model.BasePath))
            {
                _logger.LogWarning(
                    "Model directory missing for {ModelName} (ID: {ModelId}): {BasePath}",
                    model.Name, model.Id, model.BasePath);
            }
        }
    }
}
