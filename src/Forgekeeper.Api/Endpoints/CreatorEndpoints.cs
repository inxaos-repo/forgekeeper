using Forgekeeper.Core.DTOs;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Forgekeeper.Api.Endpoints;

public static class CreatorEndpoints
{
    public static void MapCreatorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/creators").WithTags("Creators");

        group.MapGet("/", async (
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            ICreatorRepository repo,
            CancellationToken ct) =>
        {
            var p  = Math.Max(1, page     ?? 1);
            var ps = Math.Clamp(pageSize ?? 100, 1, 500);

            var (creators, totalCount) = await repo.GetPagedAsync(p, ps, ct);

            var items = creators.Select(c => new CreatorResponse
            {
                Id = c.Id,
                Name = c.Name,
                Source = c.Source,
                SourceUrl = c.SourceUrl,
                AvatarUrl = c.AvatarUrl,
                ModelCount = c.ModelCount,
                CreatedAt = c.CreatedAt,
            }).ToList();

            return Results.Ok(new PaginatedResult<CreatorResponse>
            {
                Items = items,
                TotalCount = totalCount,
                Page = p,
                PageSize = ps,
            });
        }).WithName("ListCreators");

        group.MapGet("/{id:guid}", async (
            Guid id,
            ICreatorRepository creatorRepo,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            var creator = await creatorRepo.GetByIdAsync(id, ct);
            if (creator == null) return Results.NotFound();

            // Aggregate stats without loading all models into memory.
            // The paginated GET /creators/{id}/models endpoint handles the model list.
            var stats = await db.Models
                .Where(m => m.CreatorId == id)
                .GroupBy(_ => 1)
                .Select(g => new { TotalSizeBytes = (long)g.Sum(m => m.TotalSizeBytes), TotalFileCount = (long)g.Sum(m => m.FileCount) })
                .FirstOrDefaultAsync(ct);

            var response = new CreatorDetailResponse
            {
                Id = creator.Id,
                Name = creator.Name,
                Source = creator.Source,
                SourceUrl = creator.SourceUrl,
                AvatarUrl = creator.AvatarUrl,
                ModelCount = creator.ModelCount,
                CreatedAt = creator.CreatedAt,
                TotalSizeBytes = stats?.TotalSizeBytes ?? 0,
                TotalFileCount = (int)(stats?.TotalFileCount ?? 0),
                // Models NOT embedded here — use GET /creators/{id}/models for paginated model list.
                // Embedding all models causes multi-MB responses at scale (5K+ models per creator).
                Models = [],
            };

            return Results.Ok(response);
        }).WithName("GetCreator");

        group.MapGet("/{id:guid}/models", async (
            Guid id,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            [FromQuery] string? sortBy,
            [FromQuery] bool? sortDescending,
            IModelRepository modelRepo,
            ICreatorRepository creatorRepo,
            CancellationToken ct) =>
        {
            var creator = await creatorRepo.GetByIdAsync(id, ct);
            if (creator == null) return Results.NotFound();

            var (models, totalCount) = await modelRepo.GetByCreatorIdPagedAsync(
                id, page ?? 1, pageSize ?? 50, sortBy, sortDescending ?? false, ct);

            var items = models.Select(m => new ModelResponse
            {
                Id = m.Id,
                Name = m.Name,
                CreatorName = creator.Name,
                CreatorId = m.CreatorId,
                Source = m.Source,
                SourceSlug = m.SourceEntity?.Slug,
                FileCount = m.FileCount,
                TotalSizeBytes = m.TotalSizeBytes,
                ThumbnailPath = m.ThumbnailPath,
                Printed = m.Printed,
                Rating = m.Rating,
                LicenseType = m.LicenseType,
                CollectionName = m.CollectionName,
                Tags = m.Tags.Select(t => t.Name).ToList(),
                CreatedAt = m.CreatedAt,
                UpdatedAt = m.UpdatedAt,
            }).ToList();

            return Results.Ok(new PaginatedResult<ModelResponse>
            {
                Items = items,
                TotalCount = totalCount,
                Page = page ?? 1,
                PageSize = pageSize ?? 50,
            });
        }).WithName("GetCreatorModels");
    }
}
