using Forgekeeper.Core.Interfaces;

namespace Forgekeeper.Api.BackgroundServices;

public class ThumbnailWorker : BackgroundService
{
    private readonly IThumbnailService _thumbnailService;
    private readonly ILogger<ThumbnailWorker> _logger;

    public ThumbnailWorker(IThumbnailService thumbnailService, ILogger<ThumbnailWorker> logger)
    {
        _thumbnailService = thumbnailService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Thumbnail worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _thumbnailService.ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Thumbnail processing error");
            }

            // Wait 5 minutes between batches
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }

        _logger.LogInformation("Thumbnail worker stopped");
    }
}
