using Forgekeeper.Core.DTOs;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Forgekeeper.Api.Endpoints;

public static class ModelEndpoints
{
    public static void MapModelEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/models").WithTags("Models");

        group.MapGet("/", async (
            [AsParameters] ModelSearchRequest request,
            ISearchService searchService,
            CancellationToken ct) =>
        {
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
                PrintHistory = model.PrintHistory,
                Components = model.Components,
                Tags = model.Tags.Select(t => t.Name).ToList(),
                CreatedAt = model.CreatedAt,
                UpdatedAt = model.UpdatedAt,
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
            IModelRepository repo,
            CancellationToken ct) =>
        {
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
    }
}
