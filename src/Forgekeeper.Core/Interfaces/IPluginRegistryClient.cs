using Forgekeeper.Core.Models;

namespace Forgekeeper.Core.Interfaces;

/// <summary>
/// Client for fetching and querying the Forgekeeper plugin registry.
/// </summary>
public interface IPluginRegistryClient
{
    /// <summary>Fetch the full registry, using the local cache if it is fresh enough.</summary>
    Task<PluginRegistry?> FetchRegistryAsync(bool forceRefresh = false, CancellationToken ct = default);

    /// <summary>Search available plugins by name/description/tag/slug (case-insensitive).</summary>
    Task<List<PluginRegistryEntry>> SearchAsync(string? query = null, string? tag = null, CancellationToken ct = default);

    /// <summary>Look up a single plugin by exact slug.</summary>
    Task<PluginRegistryEntry?> GetPluginAsync(string slug, CancellationToken ct = default);

    /// <summary>
    /// Compare the installed versions against the registry and return available updates.
    /// Each tuple is (slug, currentVersion).
    /// </summary>
    Task<List<PluginUpdateInfo>> CheckUpdatesAsync(
        IEnumerable<(string slug, string currentVersion)> installed,
        CancellationToken ct = default);
}
