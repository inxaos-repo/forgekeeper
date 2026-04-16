using Forgekeeper.Core.DTOs;

namespace Forgekeeper.Core.Interfaces;

public interface IScannerService
{
    Task<ScanProgress> ScanAsync(bool incremental = false, CancellationToken ct = default);
    ScanProgress GetProgress();
    bool IsRunning { get; }
}
