namespace Forgekeeper.Core.Interfaces;

public interface IThumbnailService
{
    Task GenerateThumbnailAsync(string stlPath, string outputPath, CancellationToken ct = default);
    Task ProcessPendingAsync(CancellationToken ct = default);
}
