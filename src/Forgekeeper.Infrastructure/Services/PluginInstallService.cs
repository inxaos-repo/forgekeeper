using System.IO.Compression;
using System.Security.Cryptography;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// Downloads, verifies, extracts, and installs plugins from GitHub releases.
/// Manages plugin directories: install, update (with backup/rollback), and remove.
/// </summary>
public class PluginInstallService : IPluginInstallService
{
    private readonly IGitHubReleaseResolver _resolver;
    private readonly ManifestValidationService _manifestValidator;
    private readonly SdkCompatibilityChecker _sdkChecker;
    private readonly ILogger<PluginInstallService> _logger;
    private readonly string _pluginsDirectory;
    private readonly string _builtinPluginsDirectory;
    private readonly IDbContextFactory<ForgeDbContext>? _dbFactory;

    public PluginInstallService(
        IGitHubReleaseResolver resolver,
        ManifestValidationService manifestValidator,
        SdkCompatibilityChecker sdkChecker,
        IConfiguration configuration,
        ILogger<PluginInstallService> logger,
        IServiceProvider serviceProvider)
    {
        _resolver = resolver;
        _manifestValidator = manifestValidator;
        _sdkChecker = sdkChecker;
        _logger = logger;
        _pluginsDirectory = configuration["Forgekeeper:PluginsDirectory"] ?? "/data/plugins";
        _builtinPluginsDirectory = configuration["Forgekeeper:BuiltinPluginsDirectory"] ?? "/app/plugins";

        // DB factory is optional — remove still works without it (just skips config cleanup)
        _dbFactory = serviceProvider.GetService(typeof(IDbContextFactory<ForgeDbContext>))
            as IDbContextFactory<ForgeDbContext>;
    }

    /// <inheritdoc/>
    public async Task<PluginInstallResult> InstallAsync(
        string source,
        string? version = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Resolving plugin release from '{Source}' version '{Version}'", source, version ?? "latest");

        // 1. Resolve the release metadata
        var release = await _resolver.ResolveAsync(source, version, ct);
        if (release is null)
        {
            return Fail($"Could not resolve release from '{source}'. "
                + "Check the URL, version tag, and network connectivity.");
        }

        _logger.LogInformation("Resolved version {Version} — downloading from {Url}", release.Version, release.DownloadUrl);

        var tempId = Guid.NewGuid().ToString("N")[..8];
        var tempDir = Path.Combine(Path.GetTempPath(), $"forgekeeper-install-{tempId}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // 2. Download zip to temp directory
            var zipPath = Path.Combine(tempDir, "plugin.zip");
            try
            {
                await DownloadFileAsync(release.DownloadUrl, zipPath, ct);
            }
            catch (Exception ex)
            {
                return Fail($"Download failed: {ex.Message}");
            }

            // 3. Verify checksum if available
            if (release.Checksum is not null)
            {
                _logger.LogInformation("Verifying SHA-256 checksum...");
                if (!VerifyChecksum(zipPath, release.Checksum))
                {
                    _logger.LogError("Checksum mismatch for '{Source}' v{Version}", source, release.Version);
                    return Fail("Checksum verification failed — the downloaded file may be corrupted or tampered with. Refusing to install.");
                }
                _logger.LogInformation("Checksum verified ✓");
            }
            else
            {
                _logger.LogDebug("No checksum available — skipping verification");
            }

            // 4. Extract to temp dir
            var extractDir = Path.Combine(tempDir, "extracted");
            try
            {
                ZipFile.ExtractToDirectory(zipPath, extractDir);
            }
            catch (Exception ex)
            {
                return Fail($"Failed to extract plugin zip: {ex.Message}");
            }

            // 5. Find manifest.json inside the extracted archive
            // The zip may have a top-level directory (common) or be flat
            var manifestDir = FindManifestDirectory(extractDir);
            if (manifestDir is null)
            {
                return Fail("No manifest.json found in the downloaded plugin archive. "
                    + "The plugin zip must contain a manifest.json at its root or in a single top-level directory.");
            }

            // 6. Load and validate manifest
            var manifest = _manifestValidator.LoadManifest(manifestDir);
            if (manifest is null)
                return Fail("Failed to parse manifest.json from the downloaded plugin.");

            var validation = _manifestValidator.Validate(manifest);
            if (!validation.IsValid)
            {
                return Fail($"Plugin manifest is invalid: {string.Join("; ", validation.Errors)}");
            }

            if (validation.Warnings.Count > 0)
                _logger.LogWarning("Plugin '{Slug}' manifest warnings: {Warnings}", manifest.Slug, string.Join("; ", validation.Warnings));

            // 7. Check SDK compatibility — refuse if major mismatch
            var sdkCompat = _sdkChecker.CheckCompatibility(manifest);
            if (!sdkCompat.IsCompatible)
            {
                return Fail($"Plugin '{manifest.Slug}' v{manifest.Version} is SDK-incompatible and cannot be installed. "
                    + $"Reason: {sdkCompat.Reason}");
            }

            if (sdkCompat.Level == SdkCompatLevel.MinorMismatch)
            {
                _logger.LogWarning("Plugin '{Slug}' has minor SDK mismatch: {Reason}", manifest.Slug, sdkCompat.Reason);
            }

            var slug = manifest.Slug;
            var targetDir = Path.Combine(_pluginsDirectory, slug);
            string? backupDir = null;
            string? previousVersion = null;

            // 8. Backup existing version if upgrading
            if (Directory.Exists(targetDir))
            {
                var existingManifest = _manifestValidator.LoadManifest(targetDir);
                previousVersion = existingManifest?.Version;

                if (previousVersion == manifest.Version)
                    _logger.LogInformation("Reinstalling plugin '{Slug}' v{Version}", slug, manifest.Version);
                else
                    _logger.LogInformation("Upgrading plugin '{Slug}' v{Old} → v{New}", slug, previousVersion ?? "?", manifest.Version);

                backupDir = targetDir + ".bak";
                if (Directory.Exists(backupDir))
                    Directory.Delete(backupDir, true);

                Directory.Move(targetDir, backupDir);
            }

            // 9. Copy extracted files to target directory
            try
            {
                Directory.CreateDirectory(targetDir);
                CopyDirectoryRecursive(manifestDir, targetDir);

                // Success — clean up backup
                if (backupDir is not null && Directory.Exists(backupDir))
                    Directory.Delete(backupDir, true);

                _logger.LogInformation("Plugin '{Slug}' v{Version} installed to {Dir}", slug, manifest.Version, targetDir);

                return new PluginInstallResult
                {
                    Success = true,
                    Message = previousVersion is not null
                        ? $"Plugin '{slug}' updated from v{previousVersion} to v{manifest.Version}"
                        : $"Plugin '{slug}' v{manifest.Version} installed successfully",
                    Slug = slug,
                    InstalledVersion = manifest.Version,
                    PreviousVersion = previousVersion,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to copy plugin files for '{Slug}' — rolling back", slug);

                // Rollback: restore backup
                if (Directory.Exists(targetDir))
                {
                    try { Directory.Delete(targetDir, true); } catch { }
                }
                if (backupDir is not null && Directory.Exists(backupDir))
                {
                    try { Directory.Move(backupDir, targetDir); }
                    catch (Exception rollbackEx)
                    {
                        _logger.LogError(rollbackEx, "Rollback failed for '{Slug}' — plugin may be in a broken state at {Dir}", slug, targetDir);
                    }
                }

                return Fail($"Installation failed and was rolled back: {ex.Message}");
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to clean up temp directory: {Dir}", tempDir); }
        }
    }

    /// <inheritdoc/>
    public async Task<PluginInstallResult> UpdateAsync(string slug, CancellationToken ct = default)
    {
        var pluginDir = Path.Combine(_pluginsDirectory, slug);
        if (!Directory.Exists(pluginDir))
        {
            return Fail($"Plugin '{slug}' is not installed (directory not found: {pluginDir})");
        }

        var manifest = _manifestValidator.LoadManifest(pluginDir);
        if (manifest is null)
        {
            return Fail($"Plugin '{slug}' has no manifest.json — cannot auto-update. "
                + "The manifest must include a 'source_url' pointing to the GitHub repository.");
        }

        // Prefer SourceUrl, fall back to Homepage
        var source = manifest.SourceUrl ?? manifest.Homepage;
        if (string.IsNullOrEmpty(source))
        {
            return Fail($"Plugin '{slug}' manifest has no 'source_url' or 'homepage' field. "
                + "Add the GitHub repository URL to the manifest to enable auto-update.");
        }

        if (!source.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return Fail($"Plugin '{slug}' source URL '{source}' is not a GitHub URL. "
                + "Only GitHub-hosted plugins can be auto-updated.");
        }

        _logger.LogInformation("Updating plugin '{Slug}' from '{Source}'", slug, source);
        return await InstallAsync(source, version: null, ct);
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(string slug, CancellationToken ct = default)
    {
        // Refuse to remove built-in plugins
        var builtinDir = Path.Combine(_builtinPluginsDirectory, slug);
        if (Directory.Exists(builtinDir))
        {
            _logger.LogError(
                "Cannot remove built-in plugin '{Slug}'. "
                + "Disable it in config with plugins.disabled_slugs instead.", slug);
            return false;
        }

        var pluginDir = Path.Combine(_pluginsDirectory, slug);
        if (!Directory.Exists(pluginDir))
        {
            _logger.LogWarning("Plugin '{Slug}' directory not found at {Dir}", slug, pluginDir);
            return false;
        }

        // Delete plugin directory
        try
        {
            Directory.Delete(pluginDir, true);
            _logger.LogInformation("Deleted plugin directory: {Dir}", pluginDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete plugin directory: {Dir}", pluginDir);
            return false;
        }

        // Clean up backup directory if left over
        var backupDir = pluginDir + ".bak";
        if (Directory.Exists(backupDir))
        {
            try { Directory.Delete(backupDir, true); }
            catch { /* non-fatal */ }
        }

        // Clean up PluginConfig entries from DB
        if (_dbFactory is not null)
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var configs = await db.PluginConfigs
                    .Where(c => c.PluginSlug == slug)
                    .ToListAsync(ct);
                if (configs.Count > 0)
                {
                    db.PluginConfigs.RemoveRange(configs);
                    await db.SaveChangesAsync(ct);
                    _logger.LogInformation("Removed {Count} PluginConfig entries for '{Slug}'", configs.Count, slug);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to clean up PluginConfig DB entries for '{Slug}' — plugin directory was deleted", slug);
                // Non-fatal: directory is gone, DB cleanup is best-effort
            }
        }

        return true;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static PluginInstallResult Fail(string message) =>
        new() { Success = false, Message = message };

    private async Task DownloadFileAsync(string url, string destPath, CancellationToken ct)
    {
        // Use a raw HttpClient for download (not the factory client which might have short timeouts)
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(
            new System.Net.Http.Headers.ProductInfoHeaderValue("Forgekeeper", "1.0"));
        client.Timeout = TimeSpan.FromMinutes(10);

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destPath);
        await stream.CopyToAsync(file, ct);
    }

    private static bool VerifyChecksum(string filePath, string expectedSha256Hex)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha.ComputeHash(stream);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        return actual == expectedSha256Hex.ToLowerInvariant();
    }

    /// <summary>
    /// Finds the directory containing manifest.json inside an extracted zip.
    /// The zip may either be flat (manifest.json at root) or have a single top-level directory.
    /// </summary>
    private static string? FindManifestDirectory(string extractRoot)
    {
        // Case 1: manifest.json at root of extracted archive
        if (File.Exists(Path.Combine(extractRoot, "manifest.json")))
            return extractRoot;

        // Case 2: single top-level directory containing manifest.json
        var subdirs = Directory.GetDirectories(extractRoot);
        if (subdirs.Length == 1)
        {
            var subdir = subdirs[0];
            if (File.Exists(Path.Combine(subdir, "manifest.json")))
                return subdir;
        }

        // Case 3: any nested directory with manifest.json
        foreach (var f in Directory.GetFiles(extractRoot, "manifest.json", SearchOption.AllDirectories))
            return Path.GetDirectoryName(f);

        return null;
    }

    private static void CopyDirectoryRecursive(string source, string destination)
    {
        foreach (var file in Directory.GetFiles(source))
        {
            var destFile = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            var destDir = Path.Combine(destination, Path.GetFileName(dir));
            Directory.CreateDirectory(destDir);
            CopyDirectoryRecursive(dir, destDir);
        }
    }
}
