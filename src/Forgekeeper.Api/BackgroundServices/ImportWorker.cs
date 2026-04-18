using Forgekeeper.Core.Interfaces;

namespace Forgekeeper.Api.BackgroundServices;

/// <summary>
/// Periodically scans configured watch directories for new files to import.
/// Watch directories are configured via Import:WatchDirectories (array of paths).
/// Falls back to {BasePath}/unsorted/ if no watch directories configured.
/// </summary>
public class ImportWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ImportWorker> _logger;

    public ImportWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<ImportWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = _config.GetValue("Import:IntervalMinutes", 30);
        var enabled = _config.GetValue("Import:AutoImportEnabled", false);

        if (!enabled)
        {
            _logger.LogInformation("Import worker disabled (set Import:AutoImportEnabled=true to enable)");
            return;
        }

        var interval = TimeSpan.FromMinutes(intervalMinutes);
        _logger.LogInformation("Import worker started — interval: {Interval}min", intervalMinutes);

        // Initial delay to let the app finish starting
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunImportScanAsync(stoppingToken);
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Import worker error — will retry at next interval");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        _logger.LogInformation("Import worker stopped");
    }

    private async Task RunImportScanAsync(CancellationToken ct)
    {
        var watchDirs = GetWatchDirectories();
        if (watchDirs.Count == 0)
        {
            _logger.LogDebug("No watch directories configured");
            return;
        }

        _logger.LogInformation("Import scan: checking {Count} watch directories", watchDirs.Count);

        using var scope = _scopeFactory.CreateScope();
        var importService = scope.ServiceProvider.GetRequiredService<IImportService>();

        var results = await importService.ProcessDirectoriesAsync(watchDirs, ct);

        if (results.Count > 0)
        {
            var autoSorted = results.Count(r => r.Status == Core.Enums.ImportStatus.AutoSorted);
            var pending = results.Count(r => r.Status == Core.Enums.ImportStatus.AwaitingReview);
            _logger.LogInformation(
                "Import scan found {Total} items: {Auto} auto-sorted, {Pending} pending review",
                results.Count, autoSorted, pending);
        }
    }

    private List<string> GetWatchDirectories()
    {
        var dirs = new List<string>();

        // Configured watch directories
        var watchDirs = _config.GetSection("Import:WatchDirectories").Get<string[]>();
        if (watchDirs != null)
            dirs.AddRange(watchDirs.Where(d => !string.IsNullOrWhiteSpace(d)));

        // Fall back to {BasePath}/unsorted/ if no watch dirs configured
        if (dirs.Count == 0)
        {
            var basePaths = _config.GetSection("Storage:BasePaths").Get<string[]>() ?? ["/mnt/3dprinting"];
            foreach (var bp in basePaths)
            {
                var unsorted = Path.Combine(bp, "unsorted");
                if (Directory.Exists(unsorted))
                    dirs.Add(unsorted);
            }
        }

        return dirs;
    }
}
