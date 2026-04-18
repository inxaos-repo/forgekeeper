using Forgekeeper.Core.DTOs;
using Forgekeeper.Core.Enums;

namespace Forgekeeper.Core.Interfaces;

public interface IImportService
{
    Task<List<ImportQueueItemDto>> ProcessUnsortedAsync(CancellationToken ct = default);
    Task<List<ImportQueueItemDto>> ProcessDirectoriesAsync(List<string> directories, CancellationToken ct = default);
    Task<List<ImportQueueItemDto>> GetQueueAsync(ImportStatus? status = null, CancellationToken ct = default);
    Task ConfirmImportAsync(Guid queueItemId, ImportConfirmRequest request, CancellationToken ct = default);
    Task DismissAsync(Guid queueItemId, CancellationToken ct = default);
}
