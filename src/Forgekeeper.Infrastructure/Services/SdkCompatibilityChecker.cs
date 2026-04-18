using Forgekeeper.Core.Models;
using Forgekeeper.PluginSdk;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// Checks whether a plugin's declared SDK version requirements are compatible
/// with the running host SDK version.
/// </summary>
public class SdkCompatibilityChecker
{
    private readonly ILogger<SdkCompatibilityChecker> _logger;

    public SdkCompatibilityChecker(ILogger<SdkCompatibilityChecker> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Check SDK compatibility for a plugin manifest.
    /// Returns Unknown if the manifest has no SDK version info.
    /// </summary>
    public SdkCompatResult CheckCompatibility(PluginManifest manifest)
    {
        // If manifest has no SDK version fields at all, we can't know
        if (string.IsNullOrWhiteSpace(manifest.MinSdkVersion) &&
            string.IsNullOrWhiteSpace(manifest.MaxSdkVersion) &&
            string.IsNullOrWhiteSpace(manifest.SdkVersion))
        {
            return new SdkCompatResult
            {
                IsCompatible = true,
                Level = SdkCompatLevel.Unknown,
                Reason = "No SDK version declared in manifest — assuming compatible",
            };
        }

        var hostVersion = new Version(SdkInfo.MajorVersion, SdkInfo.MinorVersion, SdkInfo.PatchVersion);

        // Check min_sdk_version
        if (!string.IsNullOrWhiteSpace(manifest.MinSdkVersion))
        {
            if (Version.TryParse(manifest.MinSdkVersion, out var minVer))
            {
                if (hostVersion < minVer)
                {
                    var level = minVer.Major > hostVersion.Major
                        ? SdkCompatLevel.MajorMismatch
                        : SdkCompatLevel.MinorMismatch;

                    return new SdkCompatResult
                    {
                        IsCompatible = false,
                        Level = level,
                        Reason = $"Plugin requires SDK >= {manifest.MinSdkVersion}, host SDK is {SdkInfo.Version}",
                    };
                }
            }
            else
            {
                _logger.LogWarning("Could not parse min_sdk_version '{Ver}' — skipping min check", manifest.MinSdkVersion);
            }
        }

        // Check max_sdk_version (supports "1.x" wildcard)
        if (!string.IsNullOrWhiteSpace(manifest.MaxSdkVersion))
        {
            var maxResult = CheckMaxVersion(manifest.MaxSdkVersion, hostVersion);
            if (maxResult is not null)
                return maxResult;
        }

        // Check exact sdk_version (if neither min nor max are set, use this for major check)
        if (!string.IsNullOrWhiteSpace(manifest.SdkVersion) &&
            string.IsNullOrWhiteSpace(manifest.MinSdkVersion) &&
            string.IsNullOrWhiteSpace(manifest.MaxSdkVersion))
        {
            if (Version.TryParse(manifest.SdkVersion, out var exactVer))
            {
                if (exactVer.Major != hostVersion.Major)
                {
                    return new SdkCompatResult
                    {
                        IsCompatible = false,
                        Level = SdkCompatLevel.MajorMismatch,
                        Reason = $"Plugin built against SDK {manifest.SdkVersion} (major {exactVer.Major}), host SDK major is {hostVersion.Major}",
                    };
                }

                if (exactVer.Minor != hostVersion.Minor)
                {
                    return new SdkCompatResult
                    {
                        IsCompatible = true,
                        Level = SdkCompatLevel.MinorMismatch,
                        Reason = $"Plugin built against SDK {manifest.SdkVersion} (minor {exactVer.Minor}), host SDK minor is {hostVersion.Minor} — loading with warning",
                    };
                }
            }
        }

        return new SdkCompatResult
        {
            IsCompatible = true,
            Level = SdkCompatLevel.Compatible,
            Reason = null,
        };
    }

    /// <summary>
    /// Checks the max_sdk_version field, supporting wildcard like "1.x" or "1.2.x".
    /// Returns null if the constraint is satisfied (no problem found).
    /// </summary>
    private SdkCompatResult? CheckMaxVersion(string maxSdkVersion, Version hostVersion)
    {
        // Wildcard: "1.x" means any 1.y.z is ok
        if (ManifestValidationService.IsWildcardVersion(maxSdkVersion))
        {
            // Parse the non-wildcard prefix parts
            var parts = maxSdkVersion.Split('.');
            // Major must match
            if (int.TryParse(parts[0], out var maxMajor))
            {
                if (hostVersion.Major > maxMajor)
                {
                    return new SdkCompatResult
                    {
                        IsCompatible = false,
                        Level = SdkCompatLevel.MajorMismatch,
                        Reason = $"Plugin max_sdk_version is '{maxSdkVersion}', host SDK major is {hostVersion.Major} (too new)",
                    };
                }

                // If "1.2.x" — also check minor
                if (parts.Length == 3 && int.TryParse(parts[1], out var maxMinor))
                {
                    if (hostVersion.Major == maxMajor && hostVersion.Minor > maxMinor)
                    {
                        return new SdkCompatResult
                        {
                            IsCompatible = true, // Minor mismatch is a warning, not a block
                            Level = SdkCompatLevel.MinorMismatch,
                            Reason = $"Plugin max_sdk_version is '{maxSdkVersion}', host SDK minor is {hostVersion.Minor} — loading with warning",
                        };
                    }
                }
            }
            return null; // Wildcard satisfied
        }

        // Exact version ceiling
        if (Version.TryParse(maxSdkVersion, out var maxVer))
        {
            if (hostVersion > maxVer)
            {
                var level = hostVersion.Major > maxVer.Major
                    ? SdkCompatLevel.MajorMismatch
                    : SdkCompatLevel.MinorMismatch;

                return new SdkCompatResult
                {
                    IsCompatible = level != SdkCompatLevel.MajorMismatch,
                    Level = level,
                    Reason = $"Plugin max_sdk_version is {maxSdkVersion}, host SDK is {SdkInfo.Version} (too new)",
                };
            }
        }
        else
        {
            _logger.LogWarning("Could not parse max_sdk_version '{Ver}' — skipping max check", maxSdkVersion);
        }

        return null;
    }
}

/// <summary>Result of an SDK compatibility check.</summary>
public class SdkCompatResult
{
    public bool IsCompatible { get; init; }
    public string? Reason { get; init; }
    public SdkCompatLevel Level { get; init; }
}

/// <summary>Severity level of an SDK compatibility mismatch.</summary>
public enum SdkCompatLevel
{
    /// <summary>No version info in manifest — assume compatible.</summary>
    Unknown,
    /// <summary>Plugin and host SDK are compatible.</summary>
    Compatible,
    /// <summary>Minor version mismatch — load with warning.</summary>
    MinorMismatch,
    /// <summary>Major version mismatch — refuse to load.</summary>
    MajorMismatch,
}
