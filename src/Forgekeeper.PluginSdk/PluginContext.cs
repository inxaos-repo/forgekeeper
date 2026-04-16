using Microsoft.Extensions.Logging;

namespace Forgekeeper.PluginSdk;

/// <summary>
/// Runtime context provided to plugin methods. Contains everything a plugin needs
/// to do its work without depending on Forgekeeper internals.
/// </summary>
public class PluginContext
{
    /// <summary>
    /// Root directory for this source's files (e.g., /mnt/3dprinting/sources/mmf/).
    /// The plugin should organize creator/model subdirectories under here.
    /// </summary>
    public required string SourceDirectory { get; init; }

    /// <summary>
    /// Working directory for the current model being scraped.
    /// Set per-model during ScrapeModelAsync calls.
    /// </summary>
    public string? ModelDirectory { get; set; }

    /// <summary>
    /// Plugin configuration values loaded from the database.
    /// Keys match ConfigSchema field keys.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Config { get; init; }

    /// <summary>
    /// Pre-configured HttpClient for the plugin to use. The host manages timeouts and handlers.
    /// </summary>
    public required HttpClient HttpClient { get; init; }

    /// <summary>Logger scoped to this plugin.</summary>
    public required ILogger Logger { get; init; }

    /// <summary>Token storage for persisting auth tokens across sessions.</summary>
    public required ITokenStore TokenStore { get; init; }

    /// <summary>Progress reporter for long-running operations.</summary>
    public required IProgress<ScrapeProgress> Progress { get; init; }
}
