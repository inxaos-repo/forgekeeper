using Forgekeeper.Core.DTOs;
using Forgekeeper.Core.Models;

namespace Forgekeeper.Core.Interfaces;

public interface IModelRepository
{
    Task<Model3D?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Model3D?> GetByBasePathAsync(string basePath, CancellationToken ct = default);
    Task<List<Model3D>> GetByCreatorIdAsync(Guid creatorId, CancellationToken ct = default);
    Task<Model3D> AddAsync(Model3D model, CancellationToken ct = default);
    Task UpdateAsync(Model3D model, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<StatsResponse> GetStatsAsync(CancellationToken ct = default);
}
