using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Forgekeeper.Infrastructure.Repositories;

public class CreatorRepository : ICreatorRepository
{
    private readonly ForgeDbContext _db;

    public CreatorRepository(ForgeDbContext db)
    {
        _db = db;
    }

    public async Task<Creator?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Creators.FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<Creator?> GetByNameAndSourceAsync(string name, SourceType source, CancellationToken ct = default)
    {
        return await _db.Creators.FirstOrDefaultAsync(
            c => c.Name == name && c.Source == source, ct);
    }

    public async Task<List<Creator>> GetAllAsync(CancellationToken ct = default)
    {
        return await _db.Creators
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<Creator> AddAsync(Creator creator, CancellationToken ct = default)
    {
        _db.Creators.Add(creator);
        await _db.SaveChangesAsync(ct);
        return creator;
    }

    public async Task UpdateAsync(Creator creator, CancellationToken ct = default)
    {
        creator.UpdatedAt = DateTime.UtcNow;
        _db.Creators.Update(creator);
        await _db.SaveChangesAsync(ct);
    }
}
