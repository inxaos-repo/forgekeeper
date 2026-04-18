using Forgekeeper.Core.DTOs;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Forgekeeper.Infrastructure.Repositories;

public class ModelRepository : IModelRepository
{
    private readonly ForgeDbContext _db;
    private readonly Services.MetadataWritebackService? _writeback;

    public ModelRepository(ForgeDbContext db, Services.MetadataWritebackService? writeback = null)
    {
        _db = db;
        _writeback = writeback;
    }

    public async Task<Model3D?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Models
            .Include(m => m.Creator)
            .Include(m => m.SourceEntity)
            .Include(m => m.Variants)
            .Include(m => m.Tags)
            .Include(m => m.RelationsFrom)
                .ThenInclude(r => r.RelatedModel)
            .Include(m => m.RelationsTo)
                .ThenInclude(r => r.Model)
            .FirstOrDefaultAsync(m => m.Id == id, ct);
    }

    public async Task<Model3D?> GetByBasePathAsync(string basePath, CancellationToken ct = default)
    {
        return await _db.Models
            .Include(m => m.Variants)
            .Include(m => m.Tags)
            .FirstOrDefaultAsync(m => m.BasePath == basePath, ct);
    }

    public async Task<List<Model3D>> GetByCreatorIdAsync(Guid creatorId, CancellationToken ct = default)
    {
        return await _db.Models
            .Include(m => m.Tags)
            .Include(m => m.SourceEntity)
            .Where(m => m.CreatorId == creatorId)
            .OrderBy(m => m.Name)
            .ToListAsync(ct);
    }

    public async Task<(List<Model3D> Items, int TotalCount)> GetByCreatorIdPagedAsync(
        Guid creatorId, int page = 1, int pageSize = 50,
        string? sortBy = null, bool sortDescending = false, CancellationToken ct = default)
    {
        var query = _db.Models
            .Include(m => m.Tags)
            .Include(m => m.SourceEntity)
            .Where(m => m.CreatorId == creatorId);

        query = sortBy?.ToLowerInvariant() switch
        {
            "size" or "totalsizebytes" => sortDescending ? query.OrderByDescending(m => m.TotalSizeBytes) : query.OrderBy(m => m.TotalSizeBytes),
            "files" or "filecount" => sortDescending ? query.OrderByDescending(m => m.FileCount) : query.OrderBy(m => m.FileCount),
            "date" or "createdat" => sortDescending ? query.OrderByDescending(m => m.CreatedAt) : query.OrderBy(m => m.CreatedAt),
            "rating" => sortDescending ? query.OrderByDescending(m => m.Rating) : query.OrderBy(m => m.Rating),
            _ => sortDescending ? query.OrderByDescending(m => m.Name) : query.OrderBy(m => m.Name),
        };

        var totalCount = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task<Model3D> AddAsync(Model3D model, CancellationToken ct = default)
    {
        _db.Models.Add(model);
        await _db.SaveChangesAsync(ct);
        return model;
    }

    public async Task UpdateAsync(Model3D model, CancellationToken ct = default)
    {
        model.UpdatedAt = DateTime.UtcNow;
        _db.Models.Update(model);
        // Explicitly mark JSONB properties as modified (EF Core doesn't auto-detect JSONB changes)
        var entry = _db.Entry(model);
        entry.Property(m => m.PrintHistory).IsModified = true;
        entry.Property(m => m.Components).IsModified = true;
        entry.Property(m => m.Extra).IsModified = true;
        entry.Property(m => m.PrintSettings).IsModified = true;
        await _db.SaveChangesAsync(ct);

        // Write user-owned fields back to metadata.json (non-blocking, non-fatal)
        if (_writeback != null)
        {
            try { await _writeback.WritebackAsync(model, ct); }
            catch { /* logged inside writeback service */ }
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var model = await _db.Models.FindAsync([id], ct);
        if (model != null)
        {
            _db.Models.Remove(model);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<StatsResponse> GetStatsAsync(CancellationToken ct = default)
    {
        var stats = new StatsResponse
        {
            TotalModels = await _db.Models.CountAsync(ct),
            TotalCreators = await _db.Creators.CountAsync(ct),
            TotalFiles = await _db.Variants.CountAsync(ct),
            TotalSizeBytes = await _db.Models.SumAsync(m => m.TotalSizeBytes, ct),
            PrintedCount = await _db.Models.CountAsync(m =>
                m.PrintHistory != null && m.PrintHistory.Count > 0, ct),
            UnprintedCount = await _db.Models.CountAsync(m =>
                m.PrintHistory == null || m.PrintHistory.Count == 0, ct),
        };

        stats.ModelsBySource = await _db.Models
            .GroupBy(m => m.Source)
            .Select(g => new { Source = g.Key.ToString(), Count = g.Count() })
            .ToDictionaryAsync(x => x.Source, x => x.Count, ct);

        stats.ModelsByCategory = await _db.Models
            .Where(m => m.Category != null)
            .GroupBy(m => m.Category!)
            .Select(g => new { Category = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Category, x => x.Count, ct);

        stats.FilesByType = await _db.Variants
            .GroupBy(v => v.FileType)
            .Select(g => new { Type = g.Key.ToString(), Count = g.Count() })
            .ToDictionaryAsync(x => x.Type, x => x.Count, ct);

        stats.TopCreators = await _db.Creators
            .OrderByDescending(c => c.ModelCount)
            .Take(20)
            .Select(c => new CreatorStatsItem
            {
                Id = c.Id,
                Name = c.Name,
                ModelCount = c.ModelCount,
                TotalSizeBytes = c.Models.Sum(m => m.TotalSizeBytes)
            })
            .ToListAsync(ct);

        return stats;
    }
}
