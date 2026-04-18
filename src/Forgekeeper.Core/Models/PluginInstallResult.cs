namespace Forgekeeper.Core.Models;

/// <summary>
/// Result of a plugin install, update, or remove operation.
/// </summary>
public class PluginInstallResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Human-readable result message.</summary>
    public string? Message { get; set; }

    /// <summary>Plugin slug that was installed/updated/removed.</summary>
    public string? Slug { get; set; }

    /// <summary>Version that was installed (install/update only).</summary>
    public string? InstalledVersion { get; set; }

    /// <summary>Version that was previously installed (update only).</summary>
    public string? PreviousVersion { get; set; }
}
