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

    // Documents (PDF rulebooks/faction books) are indexed as non-printable variants so
    // PDF-only items still show their files instead of appearing empty.
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf"
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

            // #73: a library maintenance job (e.g. MMF orphan merge) rewrites metadata.json and model rows;
            // scanning at the same time re-imports those tags and collides on pk_model_tags. Defer.
            if (LibraryMaintenanceGate.IsHeld)
            {
                _logger.LogInformation("Scan deferred: library maintenance job running ({Job})", LibraryMaintenanceGate.Holder);
                return new ScanProgress { IsRunning = false, Status = "deferred", Error = $"library maintenance running: {LibraryMaintenanceGate.Holder}" };
            }

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

                // #110: pack layouts (e.g. manual/KS 3200/7. EGYPT/models/model_009/*.stl) nest deeper than
                // creator/model. When the candidate has no model files of its own, look deeper (bounded) and
                // make each lowest model-bearing folder its own model. mmf/patreon keep their existing behaviour.
                if (!IsFixedDepthSource(sourceName))
                {
                    var nested = FindNestedModelDirs(modelDir, NestedMaxDepth);
                    if (ShouldSplitContainer(modelDir, nested))
                    {
                        var creatorName = Path.GetFileName(creatorDir);
                        lock (_lock) _progress.NestedContainers++;
                        if (await db.Models.AsNoTracking().AnyAsync(m => m.BasePath == modelDir, ct))
                        {
                            // Old "giant" row for the container. Not deleted (needs boss approval); it is no
                            // longer rescanned, so it stops counting as found. Logged for cleanup.
                            lock (_lock) _progress.SupersededContainerRows++;
                            _logger.LogWarning("Container folder has a model row superseded by {Count} nested models: {Path}", nested.Count, modelDir);
                        }
                        foreach (var leaf in nested)
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                bool changed = await ProcessModelDirectoryAsync(leaf, incremental, db, sourceEntity, ct,
                                    creatorOverride: creatorName, nameOverride: NestedModelName(modelDir, leaf));
                                if (changed) pendingChanges++;
                            }
                            catch (Exception ex) { _logger.LogError(ex, "Failed to process model: {Path}", leaf); }

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
        CancellationToken ct,
        string? creatorOverride = null,
        string? nameOverride = null)
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
            lock (_lock) _progress.SkippedNoAdapter++;
            return false;
        }

        var parsed = adapter.ParseModelDirectory(modelDir);
        if (parsed == null) return false;
        if (creatorOverride != null) parsed.CreatorName = creatorOverride;
        if (nameOverride != null) parsed.ModelName = nameOverride;

        // #75/#77: a folder holding only a .part temp file and/or a stub metadata.json (interrupted
        // download) is not a model. Don't create a zero-file row for it; no ScanState is recorded,
        // so it is picked up once real files land. Existing rows are left alone.
        if (PartialDownloadCleanup.IsStubOrPartialDir(modelDir)
            && !await db.Models.AsNoTracking().AnyAsync(m => m.BasePath == modelDir, ct))
        {
            _logger.LogInformation("Skipping stub/partial-download folder (no content files): {Path}", modelDir);
            lock (_lock) _progress.SkippedStub++;
            return false;
        }

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
        // #67: the whole library lives under sources/mmf/, so the adapter claims everything. Only rows with a
        // real MMF identity (numeric/object-NNN id or myminifactory URL) are Mmf; the rest are local scans.
        if (parsed.Source == SourceType.Mmf)
            model.Source = MmfOrphanMergeService.IsMmfIdentity(model.SourceId, model.SourceUrl)
                ? SourceType.Mmf : SourceType.Manual;
        model.Description = metadata?.Description;
        model.Extra = metadata?.Extra != null ? JsonSerializer.Serialize(metadata.Extra) : null;
        model.ExternalCreatedAt = Forgekeeper.Infrastructure.Data.UtcDateTime.Normalize(metadata?.Dates?.Created);
        model.ExternalUpdatedAt = Forgekeeper.Infrastructure.Data.UtcDateTime.Normalize(metadata?.Dates?.Updated);
        // #76: never wipe a known download time just because metadata.json lacks dates.downloaded.
        model.DownloadedAt = Forgekeeper.Infrastructure.Data.UtcDateTime.Normalize(metadata?.Dates?.Downloaded) ?? model.DownloadedAt;
        model.LastScannedAt = DateTime.UtcNow;
        model.UpdatedAt = DateTime.UtcNow;

        // Denormalized fields from metadata
        model.LicenseType = metadata?.License?.Type;
        model.CollectionName = metadata?.Collection?.Name;
        model.PublishedAt = Forgekeeper.Infrastructure.Data.UtcDateTime.Normalize(metadata?.Dates?.Published);
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
        allFiles = RepairNonUtf8Names(modelDir, allFiles);
        var previewImages = new List<string>();

        foreach (var (filePath, relativePath, fileName) in allFiles)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();

            if (ImageExtensions.Contains(ext))
            {
                previewImages.Add(relativePath);
                continue;
            }

            if (!ModelFileExtensions.Contains(ext) && !DocumentExtensions.Contains(ext))
                continue;

            if (existingVariantPaths.Contains(relativePath))
                continue;

            // A filename with bytes that are not valid UTF-8 (e.g. 0xB0 from an old
            // Latin-1 zip) is enumerated with U+FFFD and can't be reopened by that name.
            // Skip that one file instead of failing the whole model.
            long fileSize;
            try { fileSize = new FileInfo(filePath).Length; }
            catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Skipping unreadable file in {ModelDir}: {File} ({Error}) — rename it to valid UTF-8 to index it",
                    modelDir, relativePath, ex.GetType().Name);
                continue;
            }

            var variant = new Variant
            {
                Id = Guid.NewGuid(),
                ModelId = model.Id,
                VariantType = DocumentExtensions.Contains(ext) ? VariantType.Other : DetectVariantType(relativePath, ext),
                FilePath = relativePath,
                FileName = fileName,
                FileType = DetectFileType(ext),
                FileSizeBytes = fileSize,
            };
            // Explicitly Added: via the navigation alone EF marks a pre-keyed child of a
            // tracked model as Modified -> UPDATE 0 rows -> DbUpdateConcurrencyException.
            db.Variants.Add(variant);
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
                if (!PartialDownloadCleanup.IsStubOrPartialDir(modelDir)) // #75: never backfill into an empty/partial folder
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

    /// <summary>
    /// #41: names that aren't valid UTF-8 come back with U+FFFD and can't be reopened.
    /// With Scanner:RepairNonUtf8Names=true they're renamed on disk to valid UTF-8 (bad
    /// bytes decoded as Windows-1252) and re-enumerated; otherwise they are logged and the
    /// per-file guard below skips them without failing the model.
    /// </summary>
    internal List<(string FullPath, string RelativePath, string FileName)> RepairNonUtf8Names(
        string modelDir, List<(string FullPath, string RelativePath, string FileName)> files)
    {
        if (!files.Any(f => RawFileNames.LooksLossy(f.RelativePath)))
            return files;

        if (!_config.GetValue("Scanner:RepairNonUtf8Names", false))
        {
            _logger.LogWarning("Model {ModelDir} has file names that aren't valid UTF-8; set Scanner:RepairNonUtf8Names=true to rename them", modelDir);
            return files;
        }

        try
        {
            foreach (var (oldName, newPath) in RawFileNames.RepairDirectory(modelDir, recursive: true))
                _logger.LogInformation("Renamed non-UTF-8 file name {Old} -> {New}", oldName, newPath);
            return ScanModelFiles(modelDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not repair non-UTF-8 names in {ModelDir}: {Error}", modelDir, ex.Message);
            return files;
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
            ".pdf" => VariantType.Other,
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
            ".pdf" => FileType.Pdf,
            _ => FileType.Other
        };
    }

    public const int NestedMaxDepth = 4;

    // Folder names that mean "variant/part of this model", not "a separate model".
    private static readonly HashSet<string> VariantFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "supported", "sup", "unsupported", "unsup", "nosup", "presupported", "pre-supported", "presup",
        "pre supported", "lys", "chitubox", "stl", "stls", "obj", "3mf", "files", "parts", "bases", "base",
        "separated", "split", "merged", "one piece", "onepiece", "printable", "print files",
    };

    // Generic container segments dropped from nested model names.
    private static readonly HashSet<string> GenericSegmentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "models", "model", "figures", "figure", "stl", "stls", "files",
    };

    private static bool IsFixedDepthSource(string sourceName) =>
        sourceName.Equals("mmf", StringComparison.OrdinalIgnoreCase)
        || sourceName.Equals("patreon", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// #110: split only clear pack layouts: leaves two+ levels down (category/models/model_009) or many
    /// sibling leaves. A model with a couple of part folders (Body/, Weapons/) stays one model.
    /// </summary>
    public static bool ShouldSplitContainer(string containerDir, List<string> nested)
    {
        if (nested.Count == 0 || (nested.Count == 1 && nested[0] == containerDir)) return false;
        if (nested.Count >= NestedMinSiblingLeaves) return true;
        return nested.Any(l => Path.GetRelativePath(containerDir, l).Replace('\\', '/').Split('/').Length >= 2);
    }

    public const int NestedMinSiblingLeaves = 10;

    private static bool HasDirectModelFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .Any(f => ModelFileExtensions.Contains(Path.GetExtension(f)));
        }
        catch { return false; }
    }

    /// <summary>
    /// #110: the folders under <paramref name="dir"/> that are models. A folder with model files of its own,
    /// or whose model-bearing children are only variant folders (supported/unsupported/...), is one model.
    /// Otherwise each model-bearing child is searched, up to <paramref name="depth"/> levels.
    /// Returns [dir] for ordinary models, [] if nothing below has model files.
    /// </summary>
    public static List<string> FindNestedModelDirs(string dir, int depth)
    {
        if (HasDirectModelFiles(dir)) return [dir];
        var children = SafeGetDirectories(dir).Where(HasModelFiles).ToList();
        if (children.Count == 0) return [];
        if (depth <= 0 || children.All(c => VariantFolderNames.Contains(Path.GetFileName(c).Trim())))
            return [dir];
        var result = new List<string>();
        foreach (var child in children)
            result.AddRange(FindNestedModelDirs(child, depth - 1));
        return result;
    }

    /// <summary>#110: "7. EGYPT &amp; THE TOMBS / model_009" from container + leaf path.</summary>
    public static string NestedModelName(string containerDir, string leafDir)
    {
        var rel = Path.GetRelativePath(containerDir, leafDir).Replace('\\', '/');
        var segs = rel.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !GenericSegmentNames.Contains(s.Trim()));
        return string.Join(" / ", new[] { Path.GetFileName(containerDir) }.Concat(segs));
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
        // Skip hidden/housekeeping dirs (.forgekeeper-reports, .trash-*, .git): they are
        // not creators or models, and indexing them created bogus model rows.
        try { return Directory.GetDirectories(path).Where(d => !Path.GetFileName(d).StartsWith('.')).ToArray(); }
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
    /// Reconcile DB records against filesystem. Models whose directory is missing are re-homed (#104):
    /// if exactly one existing folder under the source carries a metadata.json with the model's source id
    /// and no other model owns that path, BasePath is updated in place (after a folder reorg the files are
    /// still there, only the recorded path is stale). Anything unresolved is logged as missing.
    /// </summary>
    private async Task ReconcileSourceAsync(string sourceDir, ForgeDbContext db, CancellationToken ct)
    {
        // Project only the fields we need — don't load Variants at all
        var models = await db.Models
            .Where(m => m.BasePath.StartsWith(sourceDir))
            .AsNoTracking()
            .Select(m => new { m.Id, m.Name, m.BasePath, m.SourceId })
            .ToListAsync(ct);

        var missing = models.Where(m => !Directory.Exists(m.BasePath)).ToList();
        if (missing.Count == 0) return;

        var metaIndex = new SourceIdIndex();
        metaIndex.ScanMetadata(sourceDir);
        var owned = new HashSet<string>(models.Select(m => Path.TrimEndingDirectorySeparator(m.BasePath)), StringComparer.Ordinal);
        var rehomed = 0;

        foreach (var model in missing)
        {
            ct.ThrowIfCancellationRequested();
            var newPath = ResolveStalePath(model.SourceId, metaIndex, owned);
            if (newPath != null)
            {
                var entity = await db.Models.FirstOrDefaultAsync(m => m.Id == model.Id, ct);
                if (entity != null)
                {
                    _logger.LogInformation("Re-homed stale path for {ModelName} (ID: {ModelId}): {Old} -> {New}",
                        model.Name, model.Id, model.BasePath, newPath);
                    entity.BasePath = newPath;
                    entity.UpdatedAt = DateTime.UtcNow;
                    owned.Remove(Path.TrimEndingDirectorySeparator(model.BasePath));
                    owned.Add(newPath);
                    rehomed++;
                    continue;
                }
            }
            _logger.LogWarning(
                "Model directory missing for {ModelName} (ID: {ModelId}): {BasePath}",
                model.Name, model.Id, model.BasePath);
        }

        if (rehomed > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Re-homed {Count} model(s) with stale paths under {SourceDir}", rehomed, sourceDir);
        }
    }

    /// <summary>
    /// #104: pick the folder a stale DB row should point at — the single existing metadata.json folder for
    /// <paramref name="sourceId"/> that no other model already owns. Null when unknown or ambiguous.
    /// </summary>
    internal static string? ResolveStalePath(string? sourceId, SourceIdIndex metaIndex, IReadOnlySet<string> ownedPaths)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return null;
        var candidates = metaIndex.MetadataFolders(sourceId)
            .Where(Directory.Exists)
            .Where(p => !ownedPaths.Contains(Path.TrimEndingDirectorySeparator(p)))
            .ToList();
        if (candidates.Count > 1)
        {
            var withContent = candidates.Where(ModelFolderContent.HasContentFiles).ToList();
            if (withContent.Count == 1) return withContent[0];
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }
}
