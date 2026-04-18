using Forgekeeper.Core.Models;

namespace Forgekeeper.Core.Interfaces;

/// <summary>
/// Manages plugin installation, updates, and removal on disk.
/// Downloads from GitHub releases, verifies checksums, validates manifests, and manages plugin directories.
/// </summary>
public interface IPluginInstallService
{
    /// <summary>
    /// Install a plugin from a GitHub release URL or "owner/repo" shorthand.
    /// If the plugin is already installed, it is updated (old version backed up until new version loads cleanly).
    /// </summary>
    /// <param name="source">GitHub URL, "owner/repo", or "owner/repo@version".</param>
    /// <param name="version">Specific version to install. null = latest.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PluginInstallResult> InstallAsync(string source, string? version = null, CancellationToken ct = default);

    /// <summary>
    /// Update an installed plugin to the latest version using its manifest SourceUrl.
    /// </summary>
    /// <param name="slug">Plugin slug to update.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PluginInstallResult> UpdateAsync(string slug, CancellationToken ct = default);

    /// <summary>
    /// Remove an installed plugin: delete its directory and clean up PluginConfig DB entries.
    /// Built-in plugins cannot be removed.
    /// </summary>
    /// <param name="slug">Plugin slug to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>true if removed successfully, false otherwise.</returns>
    Task<bool> RemoveAsync(string slug, CancellationToken ct = default);
}
