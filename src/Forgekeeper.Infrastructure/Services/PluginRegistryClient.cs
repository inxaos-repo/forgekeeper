using System.Text.Json;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Core.Models;
using Forgekeeper.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// Fetches and caches the Forgekeeper community plugin registry (registry.json).
/// Reads from GitHub raw URL by default; caches locally with a configurable TTL.
/// Degrades gracefully: uses stale cache on network failure.
/// </summary>
public class PluginRegistryClient : IPluginRegistryClient
{
    private const string DefaultRegistryUrl =
        "https://raw.githubusercontent.com/forgekeeper/plugin-registry/main/registry.json";

    private readonly HttpClient _http;
    private readonly ILogger<PluginRegistryClient> _logger;
    private readonly string _registryUrl;
    private readonly string _cacheFilePath;
    private readonly int _cacheHours;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public PluginRegistryClient(
        HttpClient http,
        IConfiguration configuration,
        ILogger<PluginRegistryClient> logger)
    {
        _http = http;
        _logger = logger;
        _registryUrl = configuration["Plugins:RegistryUrl"] ?? DefaultRegistryUrl;
        _cacheHours = configuration.GetValue("Plugins:RegistryCacheHours", 24);

        var dataDir = configuration["Forgekeeper:DataDir"]
            ?? configuration["DataDir"]
            ?? "/data";
        _cacheFilePath = Path.Combine(dataDir, "registry-cache.json");
    }

    /// <inheritdoc />
    public async Task<PluginRegistry?> FetchRegistryAsync(
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        // Try cache first (if not force-refreshing)
        if (!forceRefresh && IsCacheFresh())
        {
            var cached = await ReadCacheAsync(ct);
            if (cached is not null)
            {
                _logger.LogDebug("Plugin registry: using cached copy ({Path})", _cacheFilePath);
                return cached;
            }
        }

        // Attempt network fetch
        try
        {
            _logger.LogDebug("Plugin registry: fetching from {Url}", _registryUrl);
            var response = await _http.GetAsync(_registryUrl, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Plugin registry: HTTP {Status} from {Url} — falling back to cache",
                    response.StatusCode, _registryUrl);
                return await ReadCacheAsync(ct);
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var registry = Deserialize(json);

            if (registry is null)
            {
                _logger.LogWarning("Plugin registry: deserialization returned null — falling back to cache");
                return await ReadCacheAsync(ct);
            }

            // Persist to disk cache
            await WriteCacheAsync(json, ct);
            _logger.LogInformation(
                "Plugin registry: fetched {Count} plugin(s) from {Url}",
                registry.Plugins.Count, _registryUrl);

            return registry;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Only fall back on genuine network failures, not user-requested cancellation
            if (ct.IsCancellationRequested) throw;

            _logger.LogWarning(ex, "Plugin registry: network error — falling back to cache");
            return await ReadCacheAsync(ct);
        }
    }

    /// <inheritdoc />
    public async Task<List<PluginRegistryEntry>> SearchAsync(
        string? query = null,
        string? tag = null,
        CancellationToken ct = default)
    {
        var registry = await FetchRegistryAsync(ct: ct);
        if (registry is null)
            return [];

        var plugins = registry.Plugins.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim().ToLowerInvariant();
            plugins = plugins.Where(p =>
                p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.Slug.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            var t = tag.Trim().ToLowerInvariant();
            plugins = plugins.Where(p =>
                p.Tags.Any(pt => pt.Equals(t, StringComparison.OrdinalIgnoreCase)));
        }

        return plugins.ToList();
    }

    /// <inheritdoc />
    public async Task<PluginRegistryEntry?> GetPluginAsync(
        string slug,
        CancellationToken ct = default)
    {
        var registry = await FetchRegistryAsync(ct: ct);
        return registry?.Plugins.FirstOrDefault(p =>
            p.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<List<PluginUpdateInfo>> CheckUpdatesAsync(
        IEnumerable<(string slug, string currentVersion)> installed,
        CancellationToken ct = default)
    {
        var registry = await FetchRegistryAsync(ct: ct);
        if (registry is null)
            return [];

        var updates = new List<PluginUpdateInfo>();
        var hostSdkMajor = SdkInfo.MajorVersion;

        foreach (var (slug, currentVersion) in installed)
        {
            var entry = registry.Plugins.FirstOrDefault(p =>
                p.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));
            if (entry is null) continue;

            if (!TryParseSemVer(entry.Version, out var registryVer)) continue;
            if (!TryParseSemVer(currentVersion, out var installedVer)) continue;

            if (registryVer <= installedVer) continue; // No update available

            // Check SDK compatibility: never update across major SDK versions
            bool isCompatible = true;
            if (!string.IsNullOrWhiteSpace(entry.MinSdkVersion) &&
                TryParseSemVer(entry.MinSdkVersion, out var minSdk))
            {
                if (minSdk.Major > hostSdkMajor)
                    isCompatible = false;
            }

            updates.Add(new PluginUpdateInfo
            {
                Slug = slug,
                CurrentVersion = currentVersion,
                AvailableVersion = entry.Version,
                DownloadUrl = entry.DownloadUrl,
                IsCompatible = isCompatible,
            });
        }

        return updates;
    }

    // ─── Private Helpers ──────────────────────────────────────────────────────

    private bool IsCacheFresh()
    {
        if (!File.Exists(_cacheFilePath)) return false;
        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_cacheFilePath);
        return age.TotalHours < _cacheHours;
    }

    private async Task<PluginRegistry?> ReadCacheAsync(CancellationToken ct)
    {
        if (!File.Exists(_cacheFilePath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(_cacheFilePath, ct);
            return Deserialize(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plugin registry: failed to read cache at {Path}", _cacheFilePath);
            return null;
        }
    }

    private async Task WriteCacheAsync(string json, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(_cacheFilePath, json, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plugin registry: failed to write cache at {Path}", _cacheFilePath);
        }
    }

    private static PluginRegistry? Deserialize(string json)
    {
        try
        {
            // Try snake_case (canonical registry format)
            var result = JsonSerializer.Deserialize<PluginRegistry>(json, _jsonOptions);
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parse a SemVer string as a <see cref="Version"/>. Returns false for malformed input.</summary>
    internal static bool TryParseSemVer(string? versionStr, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(versionStr)) return false;

        // Strip leading 'v' (e.g. "v1.0.0" → "1.0.0")
        var str = versionStr.TrimStart('v', 'V');

        // Version.TryParse requires at least "major.minor" — normalize "1.0.0"
        return Version.TryParse(str, out version!);
    }
}
