using Forgekeeper.Core.Interfaces;

namespace Forgekeeper.Api.BackgroundServices;

/// <summary>
/// Periodically scans the configured source directories for new/changed files.
/// Runs on a configurable interval (default: every 6 hours).
/// </summary>
public class ScannerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ScannerWorker> _logger;

    public ScannerWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<ScannerWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalHours = _config.GetValue("Scanner:IntervalHours", 6);
        var scanOnStartup = _config.GetValue("Scanner:ScanOnStartup", true);
        var interval = TimeSpan.FromHours(intervalHours);

        _logger.LogInformation(
            "Scanner worker started — interval: {Interval}h, scanOnStartup: {OnStartup}",
            intervalHours, scanOnStartup);

        if (scanOnStartup)
        {
            // Delay briefly to let the app finish starting
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            await RunScanAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nextRun = DateTime.UtcNow.Add(interval);
                _logger.LogInformation("Next scan at {NextRun:yyyy-MM-dd HH:mm:ss} UTC", nextRun);
                await Task.Delay(interval, stoppingToken);
                await RunScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scanner worker error — will retry at next interval");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        _logger.LogInformation("Scanner worker stopped");
    }

    private async Task RunScanAsync(CancellationToken ct)
    {
        _logger.LogInformation("=== Starting periodic scan ===");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var scanner = scope.ServiceProvider.GetRequiredService<IScannerService>();

            // Incremental scan (only process new/changed since last scan)
            var result = await scanner.ScanAsync(incremental: true, ct: ct);
            _logger.LogInformation(
                "Scan result: {Dirs} directories, {Models} models, {New} new",
                result.DirectoriesScanned, result.ModelsFound, result.NewModels);

            sw.Stop();
            _logger.LogInformation("=== Scan complete in {Elapsed:F1}s ===", sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Scan failed after {Elapsed:F1}s", sw.Elapsed.TotalSeconds);
        }
    }
}
