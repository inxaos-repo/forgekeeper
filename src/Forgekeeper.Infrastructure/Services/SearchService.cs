using Forgekeeper.Core.DTOs;
using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Forgekeeper.Infrastructure.Services;

public class SearchService : ISearchService
{
    private readonly ForgeDbContext _db;
    private readonly double _minSimilarity;

    public SearchService(ForgeDbContext db, IConfiguration config)
    {
        _db = db;
        _minSimilarity = config.GetValue("Search:MinTrigramSimilarity", 0.3);
    }

    public async Task<PaginatedResult<ModelResponse>> SearchAsync(ModelSearchRequest request, CancellationToken ct = default)
    {
        var query = _db.Models
            .Include(m => m.Creator)
            .Include(m => m.SourceEntity)
            .Include(m => m.Tags)
            .AsQueryable();

        // Hybrid search: exact substring (ILIKE) + fuzzy trigram (pg_trgm similarity)
        // Exact matches ranked first, fuzzy matches follow for typo tolerance
        string? searchTerm = null;
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            searchTerm = request.Query.Trim();
            query = query.Where(m =>
                EF.Functions.ILike(m.Name, $"%{searchTerm}%") ||
                EF.Functions.ILike(m.Creator.Name, $"%{searchTerm}%") ||
                m.Tags.Any(t => EF.Functions.ILike(t.Name, $"%{searchTerm}%")) ||
                EF.Functions.TrigramsSimilarity(m.Name, searchTerm) > _minSimilarity);
        }

        // Filters
        if (request.CreatorId.HasValue)
            query = query.Where(m => m.CreatorId == request.CreatorId.Value);

        if (!string.IsNullOrEmpty(request.Category))
            query = query.Where(m => m.Category == request.Category);

        if (!string.IsNullOrEmpty(request.GameSystem))
            query = query.Where(m => m.GameSystem == request.GameSystem);

        if (!string.IsNullOrEmpty(request.Scale))
            query = query.Where(m => m.Scale == request.Scale);

        if (request.Source.HasValue)
            query = query.Where(m => m.Source == request.Source.Value);

        if (!string.IsNullOrEmpty(request.SourceSlug))
            query = query.Where(m => m.SourceEntity != null && m.SourceEntity.Slug == request.SourceSlug);

        if (request.Printed.HasValue)
        {
            // Filter on PrintHistory presence first (translatable by all EF providers),
            // then the response mapping uses the Model3D.Printed computed property
            // which checks Result == "success" at the application layer.
            if (request.Printed.Value)
                query = query.Where(m => m.PrintHistory != null && m.PrintHistory.Count > 0);
            else
                query = query.Where(m => m.PrintHistory == null || m.PrintHistory.Count == 0);
        }

        if (request.MinRating.HasValue)
            query = query.Where(m => m.Rating >= request.MinRating.Value);

        if (request.FileType.HasValue)
            query = query.Where(m => m.Variants.Any(v => v.FileType == request.FileType.Value));

        if (!string.IsNullOrEmpty(request.LicenseType))
            query = query.Where(m => m.LicenseType == request.LicenseType);

        if (!string.IsNullOrEmpty(request.CollectionName))
            query = query.Where(m => m.CollectionName == request.CollectionName);

        // Tag filter: comma-separated list, model must have ALL specified tags
        if (!string.IsNullOrEmpty(request.Tags))
        {
            var tagNames = request.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.ToLowerInvariant())
                .ToList();
            foreach (var tagName in tagNames)
            {
                query = query.Where(m => m.Tags.Any(t => t.Name == tagName));
            }
        }

        // Creator name filter (substring match)
        if (!string.IsNullOrEmpty(request.Creator))
        {
            var creatorSearch = request.Creator.Trim();
            query = query.Where(m => EF.Functions.ILike(m.Creator.Name, $"%{creatorSearch}%"));
        }

        if (request.AcquisitionMethod.HasValue)
            query = query.Where(m => m.AcquisitionMethod == request.AcquisitionMethod.Value);

        if (request.PublishedAfter.HasValue)
            query = query.Where(m => m.PublishedAt >= request.PublishedAfter.Value);

        if (request.PublishedBefore.HasValue)
            query = query.Where(m => m.PublishedAt <= request.PublishedBefore.Value);

        // Sorting — when searching, relevance sort puts exact matches first
        if (searchTerm != null && string.IsNullOrEmpty(request.SortBy))
        {
            // Relevance sort: exact ILIKE matches first, then by trigram similarity
            query = query
                .OrderBy(m => EF.Functions.ILike(m.Name, $"%{searchTerm}%") ? 0 : 1)
                .ThenByDescending(m => EF.Functions.TrigramsSimilarity(m.Name, searchTerm))
                .ThenBy(m => m.Name);
        }
        else
        {
            query = request.SortBy?.ToLowerInvariant() switch
            {
                "date" or "createdat" => request.SortDescending
                    ? query.OrderByDescending(m => m.CreatedAt)
                    : query.OrderBy(m => m.CreatedAt),
                "filecount" => request.SortDescending
                    ? query.OrderByDescending(m => m.FileCount)
                    : query.OrderBy(m => m.FileCount),
                "size" or "totalsize" => request.SortDescending
                    ? query.OrderByDescending(m => m.TotalSizeBytes)
                    : query.OrderBy(m => m.TotalSizeBytes),
                "rating" => request.SortDescending
                    ? query.OrderByDescending(m => m.Rating)
                    : query.OrderBy(m => m.Rating),
                "creator" => request.SortDescending
                    ? query.OrderByDescending(m => m.Creator.Name)
                    : query.OrderBy(m => m.Creator.Name),
                "relevance" when searchTerm != null => query
                    .OrderBy(m => EF.Functions.ILike(m.Name, $"%{searchTerm}%") ? 0 : 1)
                    .ThenByDescending(m => EF.Functions.TrigramsSimilarity(m.Name, searchTerm)),
                _ => request.SortDescending
                    ? query.OrderByDescending(m => m.Name)
                    : query.OrderBy(m => m.Name),
            };
        }

        var totalCount = await query.CountAsync(ct);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var page = Math.Max(1, request.Page);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new ModelResponse
            {
                Id = m.Id,
                Name = m.Name,
                CreatorName = m.Creator.Name,
                CreatorId = m.CreatorId,
                Source = m.Source,
                SourceSlug = m.SourceEntity != null ? m.SourceEntity.Slug : null,
                SourceId = m.SourceId,
                SourceUrl = m.SourceUrl,
                Description = m.Description,
                Category = m.Category,
                Scale = m.Scale,
                GameSystem = m.GameSystem,
                FileCount = m.FileCount,
                TotalSizeBytes = m.TotalSizeBytes,
                ThumbnailPath = m.ThumbnailPath,
                PreviewImages = m.PreviewImages,
                BasePath = m.BasePath,
                Printed = m.PrintHistory != null && m.PrintHistory.Count > 0,
                Rating = m.Rating,
                Notes = m.Notes,
                LicenseType = m.LicenseType,
                CollectionName = m.CollectionName,
                AcquisitionMethod = m.AcquisitionMethod,
                AcquisitionOrderId = m.AcquisitionOrderId,
                PublishedAt = m.PublishedAt,
                Tags = m.Tags.Select(t => t.Name).ToList(),
                CreatedAt = m.CreatedAt,
                UpdatedAt = m.UpdatedAt,
            })
            .ToListAsync(ct);

        return new PaginatedResult<ModelResponse>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
        };
    }
}
