using System.Collections.Concurrent;
using Forgekeeper.Core.Models;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// Thread-safe singleton that tracks available plugin updates discovered by
/// <see cref="Forgekeeper.Api.BackgroundServices.PluginUpdateWorker"/>.
/// The UI polls <c>GET /api/v1/plugins/updates</c> which reads from this tracker.
/// </summary>
public class PluginUpdateTracker
{
    private readonly ConcurrentDictionary<string, PluginUpdateInfo> _availableUpdates = new();

    /// <summary>Record (or overwrite) an available update for the given slug.</summary>
    public void SetUpdate(string slug, PluginUpdateInfo info) =>
        _availableUpdates[slug] = info;

    /// <summary>Clear an update record (e.g. after the plugin was updated).</summary>
    public void ClearUpdate(string slug) =>
        _availableUpdates.TryRemove(slug, out _);

    /// <summary>Return a snapshot of all currently tracked updates.</summary>
    public IReadOnlyDictionary<string, PluginUpdateInfo> GetAvailableUpdates() =>
        _availableUpdates;

    /// <summary>Count of plugins with available updates.</summary>
    public int UpdateCount => _availableUpdates.Count;
}
