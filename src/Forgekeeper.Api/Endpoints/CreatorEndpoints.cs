using Forgekeeper.Core.DTOs;
using Forgekeeper.Core.Interfaces;

namespace Forgekeeper.Api.Endpoints;

public static class CreatorEndpoints
{
    public static void MapCreatorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/creators").WithTags("Creators");

        group.MapGet("/", async (
            ICreatorRepository repo,
            CancellationToken ct) =>
        {
            var creators = await repo.GetAllAsync(ct);
            var response = creators.Select(c => new CreatorResponse
            {
                Id = c.Id,
                Name = c.Name,
                Source = c.Source,
                SourceUrl = c.SourceUrl,
                AvatarUrl = c.AvatarUrl,
                ModelCount = c.ModelCount,
                CreatedAt = c.CreatedAt,
            }).ToList();

            return Results.Ok(response);
        }).WithName("ListCreators");

        group.MapGet("/{id:guid}", async (
            Guid id,
            ICreatorRepository creatorRepo,
            IModelRepository modelRepo,
            CancellationToken ct) =>
        {
            var creator = await creatorRepo.GetByIdAsync(id, ct);
            if (creator == null) return Results.NotFound();

            var models = await modelRepo.GetByCreatorIdAsync(id, ct);

            var response = new CreatorDetailResponse
            {
                Id = creator.Id,
                Name = creator.Name,
                Source = creator.Source,
                SourceUrl = creator.SourceUrl,
                AvatarUrl = creator.AvatarUrl,
                ModelCount = creator.ModelCount,
                CreatedAt = creator.CreatedAt,
                TotalSizeBytes = models.Sum(m => m.TotalSizeBytes),
                TotalFileCount = models.Sum(m => m.FileCount),
                Models = models.Select(m => new ModelResponse
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
                }).ToList(),
            };

            return Results.Ok(response);
        }).WithName("GetCreator");

        group.MapGet("/{id:guid}/models", async (
            Guid id,
            IModelRepository modelRepo,
            ICreatorRepository creatorRepo,
            CancellationToken ct) =>
        {
            var creator = await creatorRepo.GetByIdAsync(id, ct);
            if (creator == null) return Results.NotFound();

            var models = await modelRepo.GetByCreatorIdAsync(id, ct);
            var response = models.Select(m => new ModelResponse
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

            return Results.Ok(response);
        }).WithName("GetCreatorModels");
    }
}
