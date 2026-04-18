using Forgekeeper.Core.Models;

namespace Forgekeeper.Core.Interfaces;

/// <summary>
/// Resolves plugin release metadata from GitHub Releases API.
/// Accepts "owner/repo", "https://github.com/owner/repo", or "owner/repo@version" sources.
/// </summary>
public interface IGitHubReleaseResolver
{
    /// <summary>
    /// Resolves the release metadata for a GitHub-hosted plugin.
    /// </summary>
    /// <param name="source">GitHub URL or "owner/repo" or "owner/repo@version".</param>
    /// <param name="version">Specific version/tag to resolve. null or "latest" resolves the latest release.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Resolved release, or null if not found / rate limited / network error.</returns>
    Task<PluginRelease?> ResolveAsync(string source, string? version = null, CancellationToken ct = default);
}
