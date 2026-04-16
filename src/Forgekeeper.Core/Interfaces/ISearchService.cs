using Forgekeeper.Core.DTOs;

namespace Forgekeeper.Core.Interfaces;

public interface ISearchService
{
    Task<PaginatedResult<ModelResponse>> SearchAsync(ModelSearchRequest request, CancellationToken ct = default);
}
