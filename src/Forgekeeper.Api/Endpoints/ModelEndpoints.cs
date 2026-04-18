using System.Text.Json;
using System.Text.Json.Serialization;
using Forgekeeper.Core.DTOs;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Api.Endpoints;

public static class ModelEndpoints
{
    public static void MapModelEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/models").WithTags("Models");

        group.MapGet("/", async (
            [FromQuery] string? query,
            [FromQuery] Guid? creatorId,
            [FromQuery] string? category,
            [FromQuery] string? gameSystem,
            [FromQuery] string? scale,
            [FromQuery] string? source,
            [FromQuery] string? creator,
            [FromQuery] string? tags,
            [FromQuery] string? licenseType,
            [FromQuery] string? collectionName,
            [FromQuery] bool? printed,
            [FromQuery] int? minRating,
            [FromQuery] string? sortBy,
            [FromQuery] bool? sortDescending,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            ISearchService searchService,
            CancellationToken ct) =>
        {
            var request = new ModelSearchRequest
            {
                Query = query,
                CreatorId = creatorId,
                Category = category,
                GameSystem = gameSystem,
                Scale = scale,
                SourceSlug = source,
                Creator = creator,
                Tags = tags,
                LicenseType = licenseType,
                CollectionName = collectionName,
                Printed = printed,
                MinRating = minRating,
                SortBy = sortBy ?? "name",
                SortDescending = sortDescending ?? false,
                Page = page ?? 1,
                PageSize = pageSize ?? 50,
            };
            var result = await searchService.SearchAsync(request, ct);
            return Results.Ok(result);
        }).WithName("SearchModels");

        group.MapGet("/{id:guid}", async (
            Guid id,
            IModelRepository repo,
            CancellationToken ct) =>
        {
            var model = await repo.GetByIdAsync(id, ct);
            if (model == null) return Results.NotFound();

            var response = new ModelDetailResponse
            {
                Id = model.Id,
                Name = model.Name,
                CreatorName = model.Creator.Name,
                CreatorId = model.CreatorId,
                Source = model.Source,
                SourceSlug = model.SourceEntity?.Slug,
                SourceId = model.SourceId,
                SourceUrl = model.SourceUrl,
                Description = model.Description,
                Category = model.Category,
                Scale = model.Scale,
                GameSystem = model.GameSystem,
                FileCount = model.FileCount,
                TotalSizeBytes = model.TotalSizeBytes,
                ThumbnailPath = model.ThumbnailPath,
                PreviewImages = model.PreviewImages,
                BasePath = model.BasePath,
                Printed = model.Printed,
                Rating = model.Rating,
                Notes = model.Notes,
                LicenseType = model.LicenseType,
                CollectionName = model.CollectionName,
                AcquisitionMethod = model.AcquisitionMethod,
                AcquisitionOrderId = model.AcquisitionOrderId,
                PublishedAt = model.PublishedAt,
                PrintHistory = model.PrintHistory,
                Components = model.Components,
                PrintSettings = model.PrintSettings,
                Tags = model.Tags.Select(t => t.Name).ToList(),
                CreatedAt = model.CreatedAt,
                UpdatedAt = model.UpdatedAt,
                RelatedModels = model.RelationsFrom
                    .Select(r => new RelatedModelSummary
                    {
                        Id = r.RelatedModelId,
                        Name = r.RelatedModel?.Name ?? "",
                        ThumbnailPath = r.RelatedModel?.ThumbnailPath,
                        RelationType = r.RelationType,
                    })
                    .Concat(model.RelationsTo
                        .Select(r => new RelatedModelSummary
                        {
                            Id = r.ModelId,
                            Name = r.Model?.Name ?? "",
                            ThumbnailPath = r.Model?.ThumbnailPath,
                            RelationType = r.RelationType,
                        }))
                    .ToList(),
                Variants = model.Variants.Select(v => new VariantResponse
                {
                    Id = v.Id,
                    VariantType = v.VariantType,
                    FilePath = v.FilePath,
                    FileName = v.FileName,
                    FileType = v.FileType,
                    FileSizeBytes = v.FileSizeBytes,
                    ThumbnailPath = v.ThumbnailPath,
                    PhysicalProperties = v.PhysicalProperties,
                }).ToList(),
            };

            return Results.Ok(response);
        }).WithName("GetModel");

        group.MapPatch("/{id:guid}", async (
            Guid id,
            [FromBody] ModelUpdateRequest update,
            IModelRepository repo,
            CancellationToken ct) =>
        {
            var model = await repo.GetByIdAsync(id, ct);
            if (model == null) return Results.NotFound();

            if (update.Name != null) model.Name = update.Name;
            if (update.Category != null) model.Category = update.Category;
            if (update.Scale != null) model.Scale = update.Scale;
            if (update.GameSystem != null) model.GameSystem = update.GameSystem;
            if (update.Rating.HasValue) model.Rating = update.Rating.Value;
            if (update.Notes != null) model.Notes = update.Notes;
            // Note: Printed is now computed from PrintHistory, so update.Printed is ignored

            await repo.UpdateAsync(model, ct);
            return Results.Ok();
        }).WithName("UpdateModel");

        group.MapDelete("/{id:guid}", async (
            Guid id,
            [FromQuery] bool? deleteFiles,
            IModelRepository repo,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var model = await repo.GetByIdAsync(id, ct);
            if (model == null) return Results.NotFound();

            if (deleteFiles == true && model.Variants.Any())
            {
                var logger = loggerFactory.CreateLogger("ModelEndpoints");
                foreach (var variant in model.Variants)
                {
                    var fullPath = Path.Combine(model.BasePath, variant.FilePath);
                    if (File.Exists(fullPath))
                    {
                        try { File.Delete(fullPath); }
                        catch (Exception ex) { logger.LogWarning(ex, "Failed to delete file {Path}", fullPath); }
                    }
                }
                // Try to clean up empty parent directories
                var dirs = model.Variants
                    .Select(v => Path.GetDirectoryName(Path.Combine(model.BasePath, v.FilePath)))
                    .Distinct();
                foreach (var dir in dirs)
                {
                    if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        try { Directory.Delete(dir); }
                        catch (Exception) { }
                    }
                }
            }

            await repo.DeleteAsync(id, ct);
            return Results.NoContent();
        }).WithName("DeleteModel");

        // --- Print History ---

        group.MapPost("/{id:guid}/prints", async (
            Guid id,
            [FromBody] AddPrintRequest request,
            IModelRepository repo,
            CancellationToken ct) =>
        {
            var model = await repo.GetByIdAsync(id, ct);
            if (model == null) return Results.NotFound();

            var entry = new PrintHistoryEntry
            {
                Id = Guid.NewGuid(),
                Date = !string.IsNullOrEmpty(request.Date) ? request.Date : DateTime.UtcNow.ToString("yyyy-MM-dd"),
                Printer = request.Printer,
                Technology = request.Technology,
                Material = request.Material,
                LayerHeight = request.LayerHeight,
                Scale = request.Scale,
                Result = request.Result,
                Notes = request.Notes,
                Duration = request.Duration,
                Photos = request.Photos,
                Variant = request.Variant,
            };

            model.PrintHistory ??= [];
            model.PrintHistory.Add(entry);

            await repo.UpdateAsync(model, ct);
            return Results.Created($"/api/v1/models/{id}/prints/{entry.Id}", entry);
        }).WithName("AddPrint");

        // --- Components ---

        group.MapPut("/{id:guid}/components", async (
            Guid id,
            [FromBody] UpdateComponentsRequest request,
            IModelRepository repo,
            CancellationToken ct) =>
        {
            var model = await repo.GetByIdAsync(id, ct);
            if (model == null) return Results.NotFound();

            model.Components = request.Components;

            await repo.UpdateAsync(model, ct);
            return Results.Ok(model.Components);
        }).WithName("UpdateComponents");

        // --- Thumbnail (model-level) ---

        group.MapGet("/{id:guid}/thumbnail", async (
            Guid id,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            var model = await db.Models
                .Include(m => m.Variants)
                .FirstOrDefaultAsync(m => m.Id == id, ct);

            if (model == null) return Results.NotFound();

            // 1. Use model's own thumbnail if set
            if (!string.IsNullOrEmpty(model.ThumbnailPath) && File.Exists(model.ThumbnailPath))
                return Results.File(model.ThumbnailPath, "image/webp");

            // 2. Try first variant with a thumbnail
            var variantThumb = model.Variants
                .Where(v => !string.IsNullOrEmpty(v.ThumbnailPath))
                .Select(v => v.ThumbnailPath)
                .FirstOrDefault();

            if (variantThumb != null && File.Exists(variantThumb))
                return Results.File(variantThumb, "image/webp");

            // 3. Try first preview image
            if (model.PreviewImages.Count > 0)
            {
                var previewPath = Path.Combine(model.BasePath, model.PreviewImages[0]);
                if (File.Exists(previewPath))
                {
                    var ext = Path.GetExtension(previewPath).ToLowerInvariant();
                    var mime = ext switch
                    {
                        ".webp" => "image/webp",
                        ".png" => "image/png",
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".gif" => "image/gif",
                        _ => "application/octet-stream"
                    };
                    return Results.File(previewPath, mime);
                }
            }

            return Results.NotFound();
        }).WithName("GetModelThumbnail");

        // --- Related Models ---

        group.MapGet("/{id:guid}/related", async (
            Guid id,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            var model = await db.Models
                .Include(m => m.RelationsFrom).ThenInclude(r => r.RelatedModel)
                .Include(m => m.RelationsTo).ThenInclude(r => r.Model)
                .FirstOrDefaultAsync(m => m.Id == id, ct);

            if (model == null) return Results.NotFound();

            var related = model.RelationsFrom
                .Select(r => new RelatedModelSummary
                {
                    Id = r.RelatedModelId,
                    Name = r.RelatedModel?.Name ?? "",
                    ThumbnailPath = r.RelatedModel?.ThumbnailPath,
                    RelationType = r.RelationType,
                })
                .Concat(model.RelationsTo
                    .Select(r => new RelatedModelSummary
                    {
                        Id = r.ModelId,
                        Name = r.Model?.Name ?? "",
                        ThumbnailPath = r.Model?.ThumbnailPath,
                        RelationType = r.RelationType,
                    }))
                .ToList();

            return Results.Ok(related);
        }).WithName("GetRelatedModels");

        group.MapPost("/{id:guid}/related", async (
            Guid id,
            [FromBody] AddRelationRequest request,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            var model = await db.Models.FindAsync([id], ct);
            if (model == null) return Results.NotFound(new { message = $"Model {id} not found" });

            var relatedModel = await db.Models.FindAsync([request.RelatedModelId], ct);
            if (relatedModel == null) return Results.NotFound(new { message = $"Related model {request.RelatedModelId} not found" });

            // Check for existing relation
            var exists = await db.ModelRelations.AnyAsync(
                r => r.ModelId == id && r.RelatedModelId == request.RelatedModelId && r.RelationType == request.RelationType, ct);
            if (exists)
                return Results.Conflict(new { message = "Relation already exists" });

            var relation = new ModelRelation
            {
                Id = Guid.NewGuid(),
                ModelId = id,
                RelatedModelId = request.RelatedModelId,
                RelationType = request.RelationType,
            };

            db.ModelRelations.Add(relation);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/models/{id}/related", new RelatedModelSummary
            {
                Id = request.RelatedModelId,
                Name = relatedModel.Name,
                ThumbnailPath = relatedModel.ThumbnailPath,
                RelationType = request.RelationType,
            });
        }).WithName("AddRelation");

        // --- Bulk Update ---

        group.MapPost("/bulk", async (
            [FromBody] BulkUpdateRequest request,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            if (request.ModelIds.Count == 0)
                return Results.BadRequest(new { message = "No model IDs provided" });

            if (request.ModelIds.Count > 500)
                return Results.BadRequest(new { message = "Maximum 500 models per bulk operation" });

            var models = await db.Models
                .Include(m => m.Tags)
                .Where(m => request.ModelIds.Contains(m.Id))
                .ToListAsync(ct);

            if (models.Count == 0)
                return Results.NotFound(new { message = "No matching models found" });

            switch (request.Operation.ToLowerInvariant())
            {
                case "tag":
                    var tagName = request.Value.ToLowerInvariant().Trim();
                    var tag = await db.Tags.FirstOrDefaultAsync(t => t.Name == tagName, ct);
                    if (tag == null)
                    {
                        tag = new Tag { Id = Guid.NewGuid(), Name = tagName };
                        db.Tags.Add(tag);
                    }
                    foreach (var m in models)
                    {
                        if (!m.Tags.Any(t => t.Name == tagName))
                            m.Tags.Add(tag);
                    }
                    break;

                case "categorize":
                    foreach (var m in models) m.Category = request.Value;
                    break;

                case "setgamesystem":
                    foreach (var m in models) m.GameSystem = request.Value;
                    break;

                case "setscale":
                    foreach (var m in models) m.Scale = request.Value;
                    break;

                case "setrating":
                    if (int.TryParse(request.Value, out var rating) && rating >= 1 && rating <= 5)
                        foreach (var m in models) m.Rating = rating;
                    else
                        return Results.BadRequest(new { message = "Rating must be 1-5" });
                    break;

                case "setlicense":
                    foreach (var m in models) m.LicenseType = request.Value;
                    break;

                case "setcollection":
                    foreach (var m in models) m.CollectionName = request.Value;
                    break;

                case "setprintstatus":
                    foreach (var m in models) m.PrintStatus = request.Value;
                    break;

                case "removetag":
                    var removeTagName = request.Value.ToLowerInvariant().Trim();
                    foreach (var m in models)
                    {
                        var tagToRemove = m.Tags.FirstOrDefault(t => t.Name == removeTagName);
                        if (tagToRemove != null) m.Tags.Remove(tagToRemove);
                    }
                    break;

                case "setcreator":
                    var creatorName = request.Value.Trim();
                    var newCreator = await db.Creators
                        .FirstOrDefaultAsync(c => c.Name.ToLower() == creatorName.ToLower(), ct);
                    if (newCreator == null)
                        return Results.BadRequest(new { message = $"Creator '{creatorName}' not found" });
                    foreach (var m in models) m.CreatorId = newCreator.Id;
                    break;

                default:
                    return Results.BadRequest(new { message = $"Unknown operation: {request.Operation}" });
            }

            foreach (var m in models)
                m.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);

            return Results.Ok(new BulkUpdateResponse
            {
                AffectedCount = models.Count,
                Operation = request.Operation,
                Value = request.Value,
            });
        }).WithName("BulkUpdateModels");

        // --- Bulk Tags (add + remove) ---

        group.MapPost("/bulk-tags", async (
            [FromBody] BulkTagsRequest request,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            if (request.ModelIds.Count == 0)
                return Results.BadRequest(new { message = "No model IDs provided" });

            if (request.ModelIds.Count > 500)
                return Results.BadRequest(new { message = "Maximum 500 models per bulk operation" });

            if (request.AddTags.Count == 0 && request.RemoveTags.Count == 0)
                return Results.BadRequest(new { message = "No tags to add or remove" });

            var models = await db.Models
                .Include(m => m.Tags)
                .Where(m => request.ModelIds.Contains(m.Id))
                .ToListAsync(ct);

            if (models.Count == 0)
                return Results.NotFound(new { message = "No matching models found" });

            var tagsAdded = 0;
            var tagsRemoved = 0;

            // Add tags
            foreach (var tagName in request.AddTags.Select(t => t.ToLowerInvariant().Trim()).Distinct())
            {
                if (string.IsNullOrWhiteSpace(tagName)) continue;

                var tag = await db.Tags.FirstOrDefaultAsync(t => t.Name == tagName, ct);
                if (tag == null)
                {
                    tag = new Tag { Id = Guid.NewGuid(), Name = tagName };
                    db.Tags.Add(tag);
                }

                foreach (var m in models)
                {
                    if (!m.Tags.Any(t => t.Name == tagName))
                    {
                        m.Tags.Add(tag);
                        tagsAdded++;
                    }
                }
            }

            // Remove tags
            foreach (var tagName in request.RemoveTags.Select(t => t.ToLowerInvariant().Trim()).Distinct())
            {
                if (string.IsNullOrWhiteSpace(tagName)) continue;

                foreach (var m in models)
                {
                    var existingTag = m.Tags.FirstOrDefault(t => t.Name == tagName);
                    if (existingTag != null)
                    {
                        m.Tags.Remove(existingTag);
                        tagsRemoved++;
                    }
                }
            }

            foreach (var m in models)
                m.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);

            return Results.Ok(new BulkTagsResponse
            {
                AffectedCount = models.Count,
                TagsAdded = tagsAdded,
                TagsRemoved = tagsRemoved,
            });
        }).WithName("BulkTagModels");

        // --- Duplicates ---

        group.MapGet("/duplicates", async (
            ForgeDbContext db,
            [FromQuery] double? minSimilarity,
            [FromQuery] int? limit,
            CancellationToken ct) =>
        {
            var threshold = minSimilarity ?? 0.7;
            var maxResults = Math.Clamp(limit ?? 50, 1, 200);

            // Find models with exact duplicate names (different creators or sources)
            var nameDupes = await db.Models
                .Include(m => m.Creator)
                .GroupBy(m => m.Name.ToLower())
                .Where(g => g.Count() > 1)
                .Take(maxResults)
                .Select(g => new DuplicateGroup
                {
                    MatchType = "name",
                    Similarity = 1.0,
                    Models = g.Select(m => new DuplicateModel
                    {
                        Id = m.Id,
                        Name = m.Name,
                        CreatorName = m.Creator.Name,
                        BasePath = m.BasePath,
                        TotalSizeBytes = m.TotalSizeBytes,
                    }).ToList(),
                })
                .ToListAsync(ct);

            // Find models with duplicate file hashes (cross-source dedup)
            var hashDupes = await db.Variants
                .Where(v => v.FileHash != null && v.FileHash != "")
                .GroupBy(v => v.FileHash!)
                .Where(g => g.Count() > 1)
                .Take(maxResults)
                .Select(g => new DuplicateGroup
                {
                    MatchType = "hash",
                    Similarity = 1.0,
                    Models = g.Select(v => v.Model)
                        .Distinct()
                        .Select(m => new DuplicateModel
                        {
                            Id = m.Id,
                            Name = m.Name,
                            CreatorName = m.Creator.Name,
                            BasePath = m.BasePath,
                            TotalSizeBytes = m.TotalSizeBytes,
                        }).ToList(),
                })
                .ToListAsync(ct);

            var results = nameDupes.Concat(hashDupes).Take(maxResults).ToList();
            return Results.Ok(results);
        }).WithName("FindDuplicates");

        // --- Rename / Move ---

        group.MapPost("/rename/preview", async (
            [FromBody] RenamePreviewRequest request,
            ForgeDbContext db,
            NamingTemplateService namingService,
            CancellationToken ct) =>
        {
            if (request.ModelIds.Count == 0)
                return Results.BadRequest(new { message = "No model IDs provided" });

            if (request.ModelIds.Count > 500)
                return Results.BadRequest(new { message = "Maximum 500 models per preview operation" });

            if (string.IsNullOrWhiteSpace(request.Template))
                return Results.BadRequest(new { message = "Template is required" });

            var models = await db.Models
                .Include(m => m.Creator)
                .Where(m => request.ModelIds.Contains(m.Id))
                .ToListAsync(ct);

            var inputs = models.Select(m =>
            {
                var files = Directory.Exists(m.BasePath)
                    ? Directory.EnumerateFiles(m.BasePath, "*", SearchOption.AllDirectories).ToList()
                    : [];

                return new ModelRenameInput
                {
                    ModelId = m.Id,
                    CurrentPath = m.BasePath,
                    ModelName = m.Name,
                    CreatorName = m.Creator.Name,
                    Scale = m.Scale,
                    Source = m.Source.ToString().ToLowerInvariant(),
                    Category = m.Category,
                    GameSystem = m.GameSystem,
                    Collection = m.CollectionName,
                    DateAdded = m.CreatedAt,
                    Files = files,
                };
            }).ToList();

            var previews = namingService.PreviewRename(inputs, request.Template);
            return Results.Ok(previews);
        }).WithName("PreviewRename");

        group.MapPost("/{id:guid}/rename", async (
            Guid id,
            [FromBody] RenameRequest request,
            ForgeDbContext db,
            IModelRepository repo,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (request.NewName == null && request.NewCreator == null)
                return Results.BadRequest(new { message = "At least one of NewName or NewCreator must be provided" });

            var model = await db.Models
                .Include(m => m.Creator)
                .Include(m => m.Tags)
                .Include(m => m.Variants)
                .FirstOrDefaultAsync(m => m.Id == id, ct);

            if (model == null) return Results.NotFound();

            var logger = loggerFactory.CreateLogger("ModelEndpoints");
            var oldPath = model.BasePath;
            var parentDir = Path.GetDirectoryName(oldPath) ?? oldPath;

            // Determine new creator
            Creator? newCreator = null;
            if (request.NewCreator != null)
            {
                newCreator = await db.Creators
                    .FirstOrDefaultAsync(c => c.Name.ToLower() == request.NewCreator.ToLower(), ct);
                if (newCreator == null)
                {
                    newCreator = new Creator
                    {
                        Id = Guid.NewGuid(),
                        Name = request.NewCreator,
                        Source = model.Creator.Source,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    };
                    db.Creators.Add(newCreator);
                    await db.SaveChangesAsync(ct);
                }
            }

            // Build new path
            var newDirName = SanitizeName(request.NewName ?? model.Name);
            string newPath;
            if (newCreator != null)
            {
                // Move to a parallel creator folder under the same grandparent
                var grandparentDir = Path.GetDirectoryName(parentDir) ?? parentDir;
                var newCreatorDir = Path.Combine(grandparentDir, SanitizeName(newCreator.Name));
                newPath = Path.Combine(newCreatorDir, newDirName);
            }
            else
            {
                // Simple rename within same creator folder
                newPath = Path.Combine(parentDir, newDirName);
            }

            // Move directory on disk
            if (oldPath != newPath && Directory.Exists(oldPath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                    Directory.Move(oldPath, newPath);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to move {Old} to {New}", oldPath, newPath);
                    return Results.Problem($"Failed to move directory: {ex.Message}");
                }
            }

            // Update DB
            if (request.NewName != null) model.Name = request.NewName;
            if (newCreator != null) model.CreatorId = newCreator.Id;
            model.BasePath = newPath;
            model.UpdatedAt = DateTime.UtcNow;

            // Rewrite metadata.json in new location
            WriteMetadataJson(model, newPath, logger);

            await repo.UpdateAsync(model, ct);

            return Results.Ok(new ModelDetailResponse
            {
                Id = model.Id,
                Name = model.Name,
                CreatorName = newCreator?.Name ?? model.Creator.Name,
                CreatorId = model.CreatorId,
                Source = model.Source,
                BasePath = model.BasePath,
                UpdatedAt = model.UpdatedAt,
            });
        }).WithName("RenameModel");

        // --- Bulk Creator Reassignment ---

        group.MapPost("/bulk-creator", async (
            [FromBody] BulkCreatorRequest request,
            ForgeDbContext db,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (request.ModelIds.Count == 0)
                return Results.BadRequest(new { message = "No model IDs provided" });

            if (request.ModelIds.Count > 500)
                return Results.BadRequest(new { message = "Maximum 500 models per bulk operation" });

            if (string.IsNullOrWhiteSpace(request.CreatorName))
                return Results.BadRequest(new { message = "CreatorName is required" });

            var logger = loggerFactory.CreateLogger("ModelEndpoints");

            // Find or create target creator
            var targetCreator = await db.Creators
                .FirstOrDefaultAsync(c => c.Name.ToLower() == request.CreatorName.ToLower(), ct);

            if (targetCreator == null)
            {
                targetCreator = new Creator
                {
                    Id = Guid.NewGuid(),
                    Name = request.CreatorName,
                    Source = Forgekeeper.Core.Enums.SourceType.Manual,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                db.Creators.Add(targetCreator);
                await db.SaveChangesAsync(ct);
            }

            var models = await db.Models
                .Include(m => m.Creator)
                .Where(m => request.ModelIds.Contains(m.Id))
                .ToListAsync(ct);

            if (models.Count == 0)
                return Results.NotFound(new { message = "No matching models found" });

            var errors = new List<string>();
            var filesMoved = 0;

            foreach (var model in models)
            {
                var oldPath = model.BasePath;
                model.CreatorId = targetCreator.Id;

                if (request.MoveFiles && Directory.Exists(oldPath))
                {
                    try
                    {
                        var parentDir = Path.GetDirectoryName(oldPath) ?? oldPath;
                        var grandparentDir = Path.GetDirectoryName(parentDir) ?? parentDir;
                        var newCreatorDir = Path.Combine(grandparentDir, SanitizeName(targetCreator.Name));
                        var newPath = Path.Combine(newCreatorDir, Path.GetFileName(oldPath));

                        if (oldPath != newPath)
                        {
                            Directory.CreateDirectory(newCreatorDir);
                            Directory.Move(oldPath, newPath);
                            model.BasePath = newPath;
                            filesMoved++;

                            // Rewrite metadata.json
                            WriteMetadataJson(model, newPath, logger);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Model {model.Id} ({model.Name}): {ex.Message}");
                        logger.LogWarning(ex, "Failed to move {Path} for bulk creator reassign", oldPath);
                    }
                }

                model.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok(new BulkCreatorResponse
            {
                AffectedCount = models.Count,
                CreatorName = targetCreator.Name,
                CreatorId = targetCreator.Id,
                FilesMovedCount = filesMoved,
                Errors = errors,
            });
        }).WithName("BulkCreatorReassign");

        // --- Bulk Metadata (multi-field + tags in one request) ---

        group.MapPost("/bulk-metadata", async (
            [FromBody] BulkMetadataRequest request,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            if (request.ModelIds.Count == 0)
                return Results.BadRequest(new { message = "No model IDs provided" });

            if (request.ModelIds.Count > 500)
                return Results.BadRequest(new { message = "Maximum 500 models per bulk operation" });

            var models = await db.Models
                .Include(m => m.Tags)
                .Where(m => request.ModelIds.Contains(m.Id))
                .ToListAsync(ct);

            if (models.Count == 0)
                return Results.NotFound(new { message = "No matching models found" });

            var errors = new List<string>();
            var tagsAdded = 0;
            var tagsRemoved = 0;

            // Apply field updates
            Creator? newCreator = null;
            if (request.Fields.TryGetValue("creator", out var creatorValue) && !string.IsNullOrWhiteSpace(creatorValue))
            {
                newCreator = await db.Creators
                    .FirstOrDefaultAsync(c => c.Name.ToLower() == creatorValue.ToLower(), ct);
                if (newCreator == null)
                    errors.Add($"Creator '{creatorValue}' not found — creator field skipped");
            }

            foreach (var model in models)
            {
                if (request.Fields.TryGetValue("category", out var category) && category != null)
                    model.Category = category;
                if (request.Fields.TryGetValue("scale", out var scale) && scale != null)
                    model.Scale = scale;
                if (request.Fields.TryGetValue("gameSystem", out var gameSystem) && gameSystem != null)
                    model.GameSystem = gameSystem;
                if (request.Fields.TryGetValue("licenseType", out var licenseType) && licenseType != null)
                    model.LicenseType = licenseType;
                if (request.Fields.TryGetValue("collectionName", out var collectionName) && collectionName != null)
                    model.CollectionName = collectionName;
                if (request.Fields.TryGetValue("printStatus", out var printStatus) && printStatus != null)
                    model.PrintStatus = printStatus;
                if (request.Fields.TryGetValue("notes", out var notes) && notes != null)
                    model.Notes = notes;
                if (request.Fields.TryGetValue("rating", out var ratingStr) && ratingStr != null
                    && int.TryParse(ratingStr, out var rating) && rating >= 1 && rating <= 5)
                    model.Rating = rating;
                if (newCreator != null)
                    model.CreatorId = newCreator.Id;

                model.UpdatedAt = DateTime.UtcNow;
            }

            // Add tags
            foreach (var tagName in request.AddTags.Select(t => t.ToLowerInvariant().Trim()).Distinct())
            {
                if (string.IsNullOrWhiteSpace(tagName)) continue;
                var tag = await db.Tags.FirstOrDefaultAsync(t => t.Name == tagName, ct);
                if (tag == null)
                {
                    tag = new Tag { Id = Guid.NewGuid(), Name = tagName };
                    db.Tags.Add(tag);
                }
                foreach (var model in models)
                {
                    if (!model.Tags.Any(t => t.Name == tagName))
                    {
                        model.Tags.Add(tag);
                        tagsAdded++;
                    }
                }
            }

            // Remove tags
            foreach (var tagName in request.RemoveTags.Select(t => t.ToLowerInvariant().Trim()).Distinct())
            {
                if (string.IsNullOrWhiteSpace(tagName)) continue;
                foreach (var model in models)
                {
                    var existing = model.Tags.FirstOrDefault(t => t.Name == tagName);
                    if (existing != null)
                    {
                        model.Tags.Remove(existing);
                        tagsRemoved++;
                    }
                }
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok(new BulkMetadataResponse
            {
                AffectedCount = models.Count,
                TagsAdded = tagsAdded,
                TagsRemoved = tagsRemoved,
                Errors = errors,
            });
        }).WithName("BulkMetadata");
    }

    // -------- private helpers --------

    private static string SanitizeName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars()
            .Concat(Path.GetInvalidPathChars())
            .Distinct()
            .ToArray();
        var cleaned = string.Concat(name.Select(c => invalidChars.Contains(c) ? '_' : c));
        return cleaned.Trim().TrimStart('.').TrimEnd('.');
    }

    private static void WriteMetadataJson(Model3D model, string targetPath, ILogger logger)
    {
        try
        {
            var metadataPath = Path.Combine(targetPath, "metadata.json");
            if (!File.Exists(metadataPath)) return;

            var existing = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(metadataPath));
            // Re-serialize with updated name/creator if possible — just rewrite in place
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };
            // Build a minimal metadata update (name field)
            var dict = new Dictionary<string, object?>();
            if (existing.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in existing.EnumerateObject())
                    dict[prop.Name] = prop.Value;
            }
            dict["name"] = model.Name;
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(dict, jsonOptions));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update metadata.json at {Path}", targetPath);
        }
    }
}
