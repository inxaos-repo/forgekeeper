namespace Forgekeeper.PluginSdk;

/// <summary>
/// Core plugin interface for library scraper plugins.
/// Each plugin scrapes a single source (MMF, Thangs, Cults3D, etc.)
/// and produces metadata.json + downloaded files for Forgekeeper to index.
/// </summary>
public interface ILibraryScraper
{
    /// <summary>URL-safe slug identifying this source (e.g., "mmf", "thangs").</summary>
    string SourceSlug { get; }

    /// <summary>Human-readable source name (e.g., "MyMiniFactory").</summary>
    string SourceName { get; }

    /// <summary>Brief description of what this plugin does.</summary>
    string Description { get; }

    /// <summary>Plugin version (SemVer).</summary>
    string Version { get; }

    /// <summary>Configuration schema — describes what config fields this plugin needs.</summary>
    IReadOnlyList<PluginConfigField> ConfigSchema { get; }

    /// <summary>Whether authentication requires a browser-based flow (OAuth, cookies, etc.).</summary>
    bool RequiresBrowserAuth { get; }

    /// <summary>
    /// Authenticate with the source. Returns auth status or a URL for browser-based auth.
    /// </summary>
    Task<AuthResult> AuthenticateAsync(PluginContext context, CancellationToken ct = default);

    /// <summary>
    /// Fetch or load the user's library manifest — the list of models they own/have access to.
    /// Plugins should attempt to fetch the manifest automatically (e.g., via API or browser automation).
    /// If <paramref name="uploadedManifest"/> is provided, it is used as a fallback or override
    /// (e.g., a JSON export file uploaded manually by the user via the Plugins admin page).
    /// Returns a list of scraped model summaries.
    /// </summary>
    Task<IReadOnlyList<ScrapedModel>> FetchManifestAsync(PluginContext context, Stream? uploadedManifest = null, CancellationToken ct = default);

    /// <summary>
    /// Scrape a single model: fetch details, download files, produce metadata.
    /// The plugin writes files into context.ModelDirectory and returns a ScrapeResult.
    /// </summary>
    Task<ScrapeResult> ScrapeModelAsync(PluginContext context, ScrapedModel model, CancellationToken ct = default);

    /// <summary>
    /// Handle an OAuth/auth callback from the source. Called when the user completes browser auth.
    /// </summary>
    Task<AuthResult> HandleAuthCallbackAsync(PluginContext context, IDictionary<string, string> callbackParams, CancellationToken ct = default);

    /// <summary>
    /// Optional: return HTML for a plugin-specific admin page (config UI, manifest upload, etc.).
    /// Return null if no custom admin UI is needed.
    /// </summary>
    string? GetAdminPageHtml(PluginContext context);
}
