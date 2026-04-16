using System.Text.Json;
using System.Text.Json.Serialization;
using Forgekeeper.PluginSdk;

namespace MySourceScraper;

/// <summary>
/// Forgekeeper library scraper plugin for My Source.
///
/// TODO: Implement the scraping logic for your source platform.
/// See the Forgekeeper Plugin SDK documentation for details.
/// </summary>
public class MySourceScraperPlugin : ILibraryScraper
{
    /// <inheritdoc />
    public string SourceSlug => "mysource";

    /// <inheritdoc />
    public string SourceName => "My Source";

    /// <inheritdoc />
    public string Description => "Scrapes 3D model libraries from My Source.";

    /// <inheritdoc />
    public string Version => "0.1.0";

    /// <inheritdoc />
    public bool RequiresBrowserAuth => false;

    /// <inheritdoc />
    public IReadOnlyList<PluginConfigField> ConfigSchema =>
    [
        // TODO: Define configuration fields your plugin needs.
        // Example:
        new PluginConfigField
        {
            Key = "API_KEY",
            Label = "API Key",
            Type = PluginConfigFieldType.Secret,
            HelpText = "Your My Source API key",
            Required = true,
        },
        new PluginConfigField
        {
            Key = "USERNAME",
            Label = "Username",
            Type = PluginConfigFieldType.String,
            HelpText = "Your My Source username",
            Required = true,
        },
    ];

    /// <inheritdoc />
    public async Task<AuthResult> AuthenticateAsync(PluginContext context, CancellationToken ct = default)
    {
        // TODO: Implement authentication logic.
        // Use context.Config to read API keys and credentials.
        // Use context.TokenStore to persist/retrieve OAuth tokens.
        // Use context.HttpClient for HTTP requests.

        var apiKey = context.Config.GetValueOrDefault("API_KEY");
        if (string.IsNullOrEmpty(apiKey))
            return AuthResult.Failed("API_KEY not configured");

        // Example: validate the API key with a test request
        // var response = await context.HttpClient.GetAsync("https://api.mysource.com/me", ct);
        // if (!response.IsSuccessStatusCode)
        //     return AuthResult.Failed("Invalid API key");

        context.Logger.LogInformation("Authenticated with My Source");
        return AuthResult.Success("Authenticated successfully");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScrapedModel>> FetchManifestAsync(
        PluginContext context, Stream? uploadedManifest = null, CancellationToken ct = default)
    {
        // TODO: Fetch the user's library from the source platform.
        // Return a list of ScrapedModel summaries.

        var models = new List<ScrapedModel>();

        // Example: paginated API call
        // var page = 1;
        // while (true)
        // {
        //     var response = await context.HttpClient.GetAsync($"https://api.mysource.com/library?page={page}", ct);
        //     var data = await response.Content.ReadAsStringAsync(ct);
        //     var items = JsonSerializer.Deserialize<List<ApiModel>>(data);
        //     if (items == null || items.Count == 0) break;
        //
        //     foreach (var item in items)
        //     {
        //         models.Add(new ScrapedModel
        //         {
        //             ExternalId = item.Id,
        //             Name = item.Name,
        //             CreatorName = item.Creator,
        //         });
        //     }
        //     page++;
        // }

        context.Logger.LogInformation("Found {Count} models in library", models.Count);
        return models;
    }

    /// <inheritdoc />
    public async Task<ScrapeResult> ScrapeModelAsync(
        PluginContext context, ScrapedModel model, CancellationToken ct = default)
    {
        // TODO: Download model files and create metadata.json.
        // Files should be written to context.ModelDirectory.

        try
        {
            // Example: download files
            // var files = await FetchModelFilesAsync(context, model, ct);
            // foreach (var file in files)
            // {
            //     var filePath = Path.Combine(context.ModelDirectory, file.FileName);
            //     Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            //     await File.WriteAllBytesAsync(filePath, file.Content, ct);
            // }

            // Write metadata.json (REQUIRED)
            // var metadata = new
            // {
            //     metadataVersion = 1,
            //     source = SourceSlug,
            //     externalId = model.ExternalId,
            //     externalUrl = model.SourceUrl,
            //     name = model.Name,
            //     creator = new { displayName = model.CreatorName },
            //     dates = new { downloaded = DateTime.UtcNow },
            // };
            // var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
            // await File.WriteAllTextAsync(Path.Combine(context.ModelDirectory, "metadata.json"), json, ct);

            return ScrapeResult.Ok("metadata.json", []);
        }
        catch (Exception ex)
        {
            return ScrapeResult.Failure(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<AuthResult> HandleAuthCallbackAsync(
        PluginContext context, IDictionary<string, string> callbackParams, CancellationToken ct = default)
    {
        // TODO: Handle OAuth callback if RequiresBrowserAuth is true.
        // Parse the callback parameters and exchange for tokens.
        await Task.CompletedTask;
        return AuthResult.Failed("OAuth not implemented");
    }

    /// <inheritdoc />
    public string? GetAdminPageHtml(PluginContext context)
    {
        // TODO: Return HTML for a custom admin page, or null for no custom UI.
        return null;
    }

    // Add any private helper methods below
    // private async Task<List<ApiFile>> FetchModelFilesAsync(PluginContext context, ScrapedModel model, CancellationToken ct) { ... }
}
