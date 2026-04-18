using Forgekeeper.Core.Interfaces;
using Forgekeeper.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Api.BackgroundServices;

/// <summary>
/// Background service that periodically checks for plugin updates from the community registry.
///
/// Config keys (all under <c>Plugins:AutoUpdate</c>):
/// <list type="bullet">
///   <item><term>Enabled</term><description>Master switch — default <c>false</c>. Nothing runs when disabled.</description></item>
///   <item><term>IntervalHours</term><description>How often to check — default <c>24</c>.</description></item>
///   <item><term>Mode</term><description><c>notify</c> (default) stores results in <see cref="PluginUpdateTracker"/>; <c>apply</c> also installs compatible updates.</description></item>
/// </list>
/// </summary>
public class PluginUpdateWorker : BackgroundService
{
    private readonly bool _enabled;
    private readonly int _intervalHours;
    private readonly string _mode; // "notify" | "apply"

    private readonly IServiceProvider _services;
    private readonly ILogger<PluginUpdateWorker> _logger;
    private readonly PluginUpdateTracker _tracker;

    public PluginUpdateWorker(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<PluginUpdateWorker> logger,
        PluginUpdateTracker tracker)
    {
        _services = services;
        _logger = logger;
        _tracker = tracker;

        _enabled = configuration.GetValue("Plugins:AutoUpdate:Enabled", false);
        _intervalHours = configuration.GetValue("Plugins:AutoUpdate:IntervalHours", 24);
        _mode = configuration["Plugins:AutoUpdate:Mode"] ?? "notify";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation(
                "PluginUpdateWorker: disabled (set Plugins:AutoUpdate:Enabled=true to enable)");
            return;
        }

        _logger.LogInformation(
            "PluginUpdateWorker: started — interval {Hours}h, mode '{Mode}'",
            _intervalHours, _mode);

        // Initial delay so the host finishes starting before we hit the network
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunUpdateCheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PluginUpdateWorker: update check failed — will retry at next interval");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(_intervalHours), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("PluginUpdateWorker: stopped");
    }

    private async Task RunUpdateCheckAsync(CancellationToken ct)
    {
        // Resolve services for this check run
        var pluginHost = _services.GetService<Forgekeeper.Infrastructure.Services.PluginHostService>();
        var registryClient = _services.GetService<IPluginRegistryClient>();

        if (pluginHost is null || registryClient is null)
        {
            _logger.LogWarning("PluginUpdateWorker: required services not available — skipping check");
            return;
        }

        // Build the (slug, currentVersion) list from all loaded plugins
        var installed = pluginHost.Plugins
            .Where(kv => kv.Value.Manifest?.Version is not null)
            .Select(kv => (kv.Key, kv.Value.Manifest!.Version))
            .ToList();

        if (installed.Count == 0)
        {
            _logger.LogDebug("PluginUpdateWorker: no installed plugins with version info — skipping");
            return;
        }

        _logger.LogInformation(
            "PluginUpdateWorker: checking updates for {Count} plugin(s)…", installed.Count);

        var updates = await registryClient.CheckUpdatesAsync(installed, ct);

        // Update the tracker
        foreach (var update in updates)
            _tracker.SetUpdate(update.Slug, update);

        // Clear tracker entries for plugins that no longer have updates
        var updatedSlugs = updates.Select(u => u.Slug).ToHashSet();
        foreach (var slug in installed.Select(i => i.Item1))
        {
            if (!updatedSlugs.Contains(slug))
                _tracker.ClearUpdate(slug);
        }

        if (updates.Count == 0)
        {
            _logger.LogInformation("PluginUpdateWorker: all plugins are up to date");
            return;
        }

        _logger.LogInformation(
            "PluginUpdateWorker: {Count} update(s) available: {Slugs}",
            updates.Count,
            string.Join(", ", updates.Select(u => $"{u.Slug} ({u.CurrentVersion} → {u.AvailableVersion})")));

        // Apply mode: auto-install compatible updates only
        if (_mode.Equals("apply", StringComparison.OrdinalIgnoreCase))
        {
            await ApplyCompatibleUpdatesAsync(updates, ct);
        }
    }

    private async Task ApplyCompatibleUpdatesAsync(
        IReadOnlyList<Forgekeeper.Core.Models.PluginUpdateInfo> updates,
        CancellationToken ct)
    {
        var installService = _services.GetService<Forgekeeper.Core.Interfaces.IPluginInstallService>();
        if (installService is null)
        {
            _logger.LogWarning("PluginUpdateWorker: IPluginInstallService not available — cannot apply updates");
            return;
        }

        foreach (var update in updates.Where(u => u.IsCompatible))
        {
            try
            {
                _logger.LogInformation(
                    "PluginUpdateWorker: auto-updating '{Slug}' {From} → {To}",
                    update.Slug, update.CurrentVersion, update.AvailableVersion);

                var result = await installService.UpdateAsync(update.Slug, ct);
                if (result.Success)
                {
                    _tracker.ClearUpdate(update.Slug);
                    _logger.LogInformation(
                        "PluginUpdateWorker: '{Slug}' updated to {Ver}",
                        update.Slug, update.AvailableVersion);
                }
                else
                {
                    _logger.LogWarning(
                        "PluginUpdateWorker: auto-update of '{Slug}' failed: {Msg}",
                        update.Slug, result.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PluginUpdateWorker: error auto-updating '{Slug}'", update.Slug);
            }
        }
    }
}
