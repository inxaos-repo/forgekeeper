using System.Text.Json;
using System.Text.RegularExpressions;
using Forgekeeper.Core.Models;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// Loads and validates plugin manifest.json files.
/// </summary>
public class ManifestValidationService
{
    private readonly ILogger<ManifestValidationService> _logger;

    // SemVer: major.minor.patch with optional pre-release/build metadata
    private static readonly Regex SemVerRegex = new(
        @"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled);

    // Slug: lowercase alphanumeric and hyphens only
    private static readonly Regex SlugRegex = new(
        @"^[a-z0-9][a-z0-9-]*[a-z0-9]$|^[a-z0-9]$",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public ManifestValidationService(ILogger<ManifestValidationService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Reads and deserializes manifest.json from the given plugin directory.
    /// Returns null if the file does not exist or cannot be parsed.
    /// </summary>
    public PluginManifest? LoadManifest(string pluginDirectory)
    {
        var manifestPath = Path.Combine(pluginDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            _logger.LogDebug("No manifest.json found in {Dir}", pluginDirectory);
            return null;
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);
            if (manifest is null)
                _logger.LogWarning("manifest.json in {Dir} deserialized to null", pluginDirectory);
            return manifest;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse manifest.json in {Dir}", pluginDirectory);
            return null;
        }
    }

    /// <summary>
    /// Validates a parsed manifest. Checks required fields, SemVer format, and slug format.
    /// </summary>
    public ManifestValidationResult Validate(PluginManifest manifest)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // Required fields
        if (string.IsNullOrWhiteSpace(manifest.Slug))
            errors.Add("'slug' is required");
        else if (!SlugRegex.IsMatch(manifest.Slug))
            errors.Add($"'slug' must be lowercase alphanumeric with hyphens only (got: '{manifest.Slug}')");

        if (string.IsNullOrWhiteSpace(manifest.Name))
            errors.Add("'name' is required");

        if (string.IsNullOrWhiteSpace(manifest.Version))
            errors.Add("'version' is required");
        else if (!SemVerRegex.IsMatch(manifest.Version))
            errors.Add($"'version' must be valid SemVer (got: '{manifest.Version}')");

        if (string.IsNullOrWhiteSpace(manifest.Author))
            warnings.Add("'author' is not set");

        if (string.IsNullOrWhiteSpace(manifest.Description))
            warnings.Add("'description' is not set");

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
            errors.Add("'entry_assembly' is required");

        // SdkVersion / version range fields — validate if present
        if (!string.IsNullOrWhiteSpace(manifest.SdkVersion) && !SemVerRegex.IsMatch(manifest.SdkVersion))
            errors.Add($"'sdk_version' must be valid SemVer (got: '{manifest.SdkVersion}')");

        if (!string.IsNullOrWhiteSpace(manifest.MinSdkVersion) && !SemVerRegex.IsMatch(manifest.MinSdkVersion))
            errors.Add($"'min_sdk_version' must be valid SemVer (got: '{manifest.MinSdkVersion}')");

        // max_sdk_version allows wildcard like "1.x" or "1.2.x"
        if (!string.IsNullOrWhiteSpace(manifest.MaxSdkVersion) &&
            !SemVerRegex.IsMatch(manifest.MaxSdkVersion) &&
            !IsWildcardVersion(manifest.MaxSdkVersion))
        {
            errors.Add($"'max_sdk_version' must be valid SemVer or wildcard (e.g. '1.x') (got: '{manifest.MaxSdkVersion}')");
        }

        if (!string.IsNullOrWhiteSpace(manifest.MinForgekeeperVersion) &&
            !SemVerRegex.IsMatch(manifest.MinForgekeeperVersion))
        {
            warnings.Add($"'min_forgekeeper_version' is not valid SemVer (got: '{manifest.MinForgekeeperVersion}')");
        }

        return new ManifestValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings,
        };
    }

    /// <summary>Returns true for wildcard versions like "1.x" or "1.2.x".</summary>
    public static bool IsWildcardVersion(string version)
    {
        // Accepts: "1.x", "1.2.x", "1.x.x"
        return Regex.IsMatch(version, @"^\d+(\.\d+)*\.x$|^\d+\.x$");
    }
}

/// <summary>Result of manifest validation.</summary>
public class ManifestValidationResult
{
    public bool IsValid { get; init; }
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}
