using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// Resolves plugin release metadata from GitHub Releases API.
/// Supports "owner/repo", "https://github.com/owner/repo", and "owner/repo@version" sources.
/// Caches resolved releases for 1 hour to avoid hammering the GitHub API.
/// </summary>
public class GitHubReleaseResolver : IGitHubReleaseResolver
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubReleaseResolver> _logger;
    private readonly string? _githubToken;

    // Cache: key = "owner/repo@version", value = (release, cachedAt)
    private readonly ConcurrentDictionary<string, (PluginRelease Release, DateTime CachedAt)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    public GitHubReleaseResolver(
        IHttpClientFactory httpClientFactory,
        ILogger<GitHubReleaseResolver> logger,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _githubToken = configuration["Plugins:GitHubToken"];
    }

    /// <inheritdoc/>
    public async Task<PluginRelease?> ResolveAsync(string source, string? version = null, CancellationToken ct = default)
    {
        // Extract version embedded in source string ("owner/repo@1.0.0")
        var atIdx = source.IndexOf('@');
        if (atIdx >= 0 && string.IsNullOrEmpty(version))
        {
            version = source[(atIdx + 1)..];
            source = source[..atIdx];
        }

        var (owner, repo) = ParseSource(source);
        if (owner is null || repo is null)
        {
            _logger.LogError("Cannot parse GitHub source: {Source}", source);
            return null;
        }

        var resolvedVersion = string.IsNullOrEmpty(version) ? "latest" : version.TrimStart('v');
        var cacheKey = $"{owner}/{repo}@{resolvedVersion}";

        // Check cache
        if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.CachedAt < CacheTtl)
        {
            _logger.LogDebug("Cache hit for {Key}", cacheKey);
            return cached.Release;
        }

        // Build GitHub API URL
        var apiUrl = resolvedVersion == "latest"
            ? $"https://api.github.com/repos/{owner}/{repo}/releases/latest"
            : $"https://api.github.com/repos/{owner}/{repo}/releases/tags/v{resolvedVersion}";

        _logger.LogDebug("Resolving GitHub release: {Url}", apiUrl);

        HttpResponseMessage response;
        try
        {
            var client = _httpClientFactory.CreateClient("github");
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Forgekeeper", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            if (!string.IsNullOrEmpty(_githubToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _githubToken);

            response = await client.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error resolving GitHub release for {Repo}", $"{owner}/{repo}");
            return null;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden ||
            response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1);
            _logger.LogWarning(
                "GitHub rate limit hit for {Repo}. Retry after {RetryAfter}. "
                + "Set Plugins:GitHubToken in config for higher rate limits.",
                $"{owner}/{repo}", retryAfter);
            return null;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogError("Release not found for {Repo} version '{Version}'", $"{owner}/{repo}", resolvedVersion);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("GitHub API returned {Status} for {Url}", response.StatusCode, apiUrl);
            return null;
        }

        string json;
        try
        {
            json = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read GitHub response for {Repo}", $"{owner}/{repo}");
            return null;
        }

        JsonElement releaseJson;
        try
        {
            releaseJson = JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse GitHub release JSON for {Repo}", $"{owner}/{repo}");
            return null;
        }

        // Parse assets
        string? downloadUrl = null;
        long? sizeBytes = null;
        string? checksumUrl = null;
        string? zipFileName = null;

        if (releaseJson.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                var size = asset.TryGetProperty("size", out var s) ? (long?)s.GetInt64() : null;

                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && downloadUrl is null)
                {
                    downloadUrl = url;
                    sizeBytes = size;
                    zipFileName = name;
                }

                if (name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("checksums.txt", StringComparison.OrdinalIgnoreCase))
                {
                    checksumUrl = url;
                }
            }
        }

        if (downloadUrl is null)
        {
            _logger.LogError(
                "No .zip asset found in {Repo} release '{Version}'. "
                + "Plugin releases must include a zip asset containing manifest.json and the plugin DLL.",
                $"{owner}/{repo}", resolvedVersion);
            return null;
        }

        var tagName = releaseJson.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "";
        var parsedVersion = tagName.TrimStart('v');
        if (string.IsNullOrEmpty(parsedVersion))
            parsedVersion = resolvedVersion;

        var publishedAt = DateTime.UtcNow;
        if (releaseJson.TryGetProperty("published_at", out var pub) && pub.ValueKind == JsonValueKind.String)
            DateTime.TryParse(pub.GetString(), out publishedAt);

        var release = new PluginRelease
        {
            Version = parsedVersion,
            DownloadUrl = downloadUrl,
            ChecksumUrl = checksumUrl,
            SizeBytes = sizeBytes,
            PublishedAt = publishedAt,
            TagName = tagName,
        };

        // Eagerly download and parse checksum file
        if (checksumUrl is not null && zipFileName is not null)
        {
            var checksum = await DownloadChecksumAsync(checksumUrl, zipFileName, ct);
            if (checksum is not null)
                release.Checksum = checksum;
        }

        _cache[cacheKey] = (release, DateTime.UtcNow);
        return release;
    }

    private async Task<string?> DownloadChecksumAsync(string checksumUrl, string zipName, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("github");
            var content = await client.GetStringAsync(checksumUrl, ct);

            // Format: "<sha256hex>  <filename>\n" (sha256sum / shasum output format)
            foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // Split on whitespace — first token is hash, second is filename (may have leading * for binary mode)
                var parts = trimmed.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var hashPart = parts[0].Trim();
                var namePart = parts[1].Trim().TrimStart('*');

                if (namePart.Equals(zipName, StringComparison.OrdinalIgnoreCase))
                    return hashPart.ToLowerInvariant();
            }

            _logger.LogWarning("Checksum for '{ZipName}' not found in SHA256SUMS file", zipName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download checksum file from {Url}", checksumUrl);
        }
        return null;
    }

    /// <summary>
    /// Parses a source string into (owner, repo).
    /// Accepts "owner/repo", "https://github.com/owner/repo", "https://github.com/owner/repo.git".
    /// </summary>
    private static (string? Owner, string? Repo) ParseSource(string source)
    {
        // Strip git suffix
        source = source.TrimEnd('/');
        if (source.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            source = source[..^4];

        // Strip GitHub URL prefix
        const string httpsPrefix = "https://github.com/";
        const string httpPrefix = "http://github.com/";
        if (source.StartsWith(httpsPrefix, StringComparison.OrdinalIgnoreCase))
            source = source[httpsPrefix.Length..];
        else if (source.StartsWith(httpPrefix, StringComparison.OrdinalIgnoreCase))
            source = source[httpPrefix.Length..];

        var parts = source.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return (null, null);

        return (parts[0], parts[1]);
    }
}
