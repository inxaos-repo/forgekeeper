using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Models;

namespace Forgekeeper.Core.Interfaces;

public interface ICreatorRepository
{
    Task<Creator?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Creator?> GetByNameAndSourceAsync(string name, SourceType source, CancellationToken ct = default);
    Task<List<Creator>> GetAllAsync(CancellationToken ct = default);
    Task<(List<Creator> Items, int TotalCount)> GetPagedAsync(int page, int pageSize, CancellationToken ct = default);
    Task<Creator> AddAsync(Creator creator, CancellationToken ct = default);
    Task UpdateAsync(Creator creator, CancellationToken ct = default);
}
