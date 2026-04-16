using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Forgekeeper.PluginSdk;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Scraper.Mmf;

/// <summary>
/// MyMiniFactory library scraper plugin.
/// 
/// Authentication: OAuth implicit flow via MMF's API.
/// Manifest: Uploaded by the user (data-library API requires browser cookies, 
///           so we accept the JSON export from the MMF data-library page).
/// Scraping: Uses MMF v2 API for model details and file downloads.
/// </summary>
public class MmfScraperPlugin : ILibraryScraper
{
    private const string MmfApiBase = "https://www.myminifactory.com/api/v2";
    private const string MmfAuthBase = "https://auth.myminifactory.com";

    public string SourceSlug => "mmf";
    public string SourceName => "MyMiniFactory";
    public string Description => "Scrapes your MyMiniFactory purchased/backed library, downloads model files, and generates metadata.json sidecar files.";
    public string Version => "1.0.0";
    public bool RequiresBrowserAuth => true;

    public IReadOnlyList<PluginConfigField> ConfigSchema =>
    [
        new PluginConfigField
        {
            Key = "CLIENT_ID",
            Label = "OAuth Client ID",
            Type = PluginConfigFieldType.String,
            Required = true,
            HelpText = "Your MMF API application's client ID.",
        },
        new PluginConfigField
        {
            Key = "CLIENT_SECRET",
            Label = "OAuth Client Secret",
            Type = PluginConfigFieldType.Secret,
            Required = true,
            HelpText = "Your MMF API application's client secret.",
        },
        new PluginConfigField
        {
            Key = "DELAY_MS",
            Label = "Request Delay (ms)",
            Type = PluginConfigFieldType.Number,
            Required = false,
            DefaultValue = "1000",
            HelpText = "Delay between API requests to avoid rate limiting.",
        },
    ];

    public async Task<AuthResult> AuthenticateAsync(PluginContext context, CancellationToken ct = default)
    {
        // Check for existing access token
        var accessToken = await context.TokenStore.GetTokenAsync("access_token", ct);
        if (!string.IsNullOrEmpty(accessToken))
        {
            // Verify the token is still valid
            try
            {
                context.HttpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                var response = await context.HttpClient.GetAsync($"{MmfApiBase}/users/me", ct);
                if (response.IsSuccessStatusCode)
                {
                    context.Logger.LogInformation("Authenticated with existing token");
                    return AuthResult.Success("Authenticated with stored token");
                }
            }
            catch
            {
                // Token invalid, proceed to re-auth
            }
        }

        // Build OAuth authorization URL
        if (!context.Config.TryGetValue("CLIENT_ID", out var clientId) || string.IsNullOrEmpty(clientId))
            return AuthResult.Failed("CLIENT_ID not configured");

        // OAuth implicit flow — user visits this URL, gets redirected back with token
        var authUrl = $"{MmfAuthBase}/web/authorize" +
                      $"?client_id={Uri.EscapeDataString(clientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(GetCallbackUrl(context))}" +
                      "&response_type=token";

        return AuthResult.NeedsBrowser(authUrl, "Visit the authorization URL to connect your MyMiniFactory account.");
    }

    public async Task<AuthResult> HandleAuthCallbackAsync(PluginContext context, IDictionary<string, string> callbackParams, CancellationToken ct = default)
    {
        // OAuth implicit flow returns access_token as a fragment parameter
        if (callbackParams.TryGetValue("access_token", out var accessToken) && !string.IsNullOrEmpty(accessToken))
        {
            await context.TokenStore.SaveTokenAsync("access_token", accessToken, ct);
            context.Logger.LogInformation("Saved MMF access token from callback");
            return AuthResult.Success("Connected to MyMiniFactory successfully!");
        }

        if (callbackParams.TryGetValue("error", out var error))
        {
            var errorDesc = callbackParams.TryGetValue("error_description", out var desc) ? desc : "";
            return AuthResult.Failed($"Auth error: {error} — {errorDesc}");
        }

        return AuthResult.Failed("No access token received in callback");
    }

    public async Task<IReadOnlyList<ScrapedModel>> FetchManifestAsync(PluginContext context, Stream? uploadedManifest = null, CancellationToken ct = default)
    {
        context.Progress.Report(new ScrapeProgress
        {
            Status = "fetching_manifest",
            CurrentItem = "Loading library manifest...",
        });

        // The data-library API requires browser cookies and can't be called with API tokens alone.
        // Users upload their manifest JSON (exported from the MMF data library page).
        if (uploadedManifest is not null)
        {
            return await ParseUploadedManifestAsync(uploadedManifest, ct);
        }

        // Try to fetch via API if we have a token (works for purchased items)
        var accessToken = await context.TokenStore.GetTokenAsync("access_token", ct);
        if (string.IsNullOrEmpty(accessToken))
        {
            context.Logger.LogWarning("No access token and no uploaded manifest — cannot fetch library");
            return [];
        }

        return await FetchLibraryViaApiAsync(context, accessToken, ct);
    }

    public async Task<ScrapeResult> ScrapeModelAsync(PluginContext context, ScrapedModel model, CancellationToken ct = default)
    {
        var modelDir = context.ModelDirectory
            ?? throw new InvalidOperationException("ModelDirectory not set in context");

        var delayMs = GetDelayMs(context);
        var accessToken = await context.TokenStore.GetTokenAsync("access_token", ct);

        context.Progress.Report(new ScrapeProgress
        {
            Status = "downloading",
            CurrentItem = model.Name,
        });

        try
        {
            // Fetch model details from v2 API
            MmfModelDetails? details = null;
            if (!string.IsNullOrEmpty(accessToken))
            {
                context.HttpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                var response = await context.HttpClient.GetAsync(
                    $"{MmfApiBase}/objects/{model.ExternalId}", ct);

                if (response.IsSuccessStatusCode)
                {
                    details = await response.Content.ReadFromJsonAsync<MmfModelDetails>(
                        JsonOptions, ct);
                }

                await Task.Delay(delayMs, ct);
            }

            // Download files
            var downloadedFiles = new List<DownloadedFile>();
            if (details?.Files is { Count: > 0 })
            {
                foreach (var file in details.Files)
                {
                    if (ct.IsCancellationRequested) break;
                    if (string.IsNullOrEmpty(file.DownloadUrl)) continue;

                    var variant = DetectVariant(file.Filename);
                    var variantDir = variant != null
                        ? Path.Combine(modelDir, variant)
                        : modelDir;
                    Directory.CreateDirectory(variantDir);

                    var filePath = Path.Combine(variantDir, SanitizeFilename(file.Filename));

                    // Skip if already downloaded
                    if (File.Exists(filePath) && new FileInfo(filePath).Length == file.Size)
                    {
                        downloadedFiles.Add(new DownloadedFile
                        {
                            Filename = file.Filename,
                            LocalPath = filePath,
                            Size = file.Size,
                            Variant = variant,
                            IsArchive = IsArchiveFile(file.Filename),
                        });
                        continue;
                    }

                    try
                    {
                        var fileResponse = await context.HttpClient.GetAsync(file.DownloadUrl, ct);
                        fileResponse.EnsureSuccessStatusCode();
                        await using var fs = File.Create(filePath);
                        await fileResponse.Content.CopyToAsync(fs, ct);

                        downloadedFiles.Add(new DownloadedFile
                        {
                            Filename = file.Filename,
                            LocalPath = filePath,
                            Size = new FileInfo(filePath).Length,
                            Variant = variant,
                            IsArchive = IsArchiveFile(file.Filename),
                        });
                    }
                    catch (Exception ex)
                    {
                        context.Logger.LogWarning("Failed to download {File}: {Error}", file.Filename, ex.Message);
                    }

                    await Task.Delay(delayMs, ct);
                }
            }

            // Write metadata.json
            var metadata = BuildMetadata(model, details, downloadedFiles);
            var metadataPath = Path.Combine(modelDir, "metadata.json");
            var json = JsonSerializer.Serialize(metadata, JsonWriteOptions);
            await File.WriteAllTextAsync(metadataPath, json, ct);

            return ScrapeResult.Ok("metadata.json", downloadedFiles);
        }
        catch (Exception ex)
        {
            return ScrapeResult.Failure($"Error scraping {model.Name}: {ex.Message}");
        }
    }

    public string? GetAdminPageHtml(PluginContext context)
    {
        return """
            <div class="plugin-admin">
                <h3>MyMiniFactory Plugin</h3>
                <p>To import your MMF library:</p>
                <ol>
                    <li>Go to <a href="https://www.myminifactory.com/my-library" target="_blank">My Library</a> on MMF</li>
                    <li>Open browser DevTools → Network tab</li>
                    <li>Look for the <code>data-library</code> API call</li>
                    <li>Copy the response JSON</li>
                    <li>Upload it here as your manifest</li>
                </ol>
                <form method="POST" enctype="multipart/form-data" action="/api/v1/plugins/mmf/manifest">
                    <input type="file" name="manifest" accept=".json" />
                    <button type="submit">Upload Manifest</button>
                </form>
            </div>
            """;
    }

    // --- Private helpers ---

    private static async Task<IReadOnlyList<ScrapedModel>> ParseUploadedManifestAsync(Stream manifestStream, CancellationToken ct)
    {
        var doc = await JsonDocument.ParseAsync(manifestStream, cancellationToken: ct);
        var models = new List<ScrapedModel>();

        // MMF data-library exports vary in structure.
        // Handle both array-of-objects and { items: [...] } formats.
        JsonElement items;
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            items = doc.RootElement;
        else if (doc.RootElement.TryGetProperty("items", out var itemsProp))
            items = itemsProp;
        else if (doc.RootElement.TryGetProperty("objects", out var objectsProp))
            items = objectsProp;
        else
            return models;

        foreach (var item in items.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idProp) ? idProp.ToString() : null;
            var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            if (id is null || name is null) continue;

            var creatorName = item.TryGetProperty("designer", out var designer)
                && designer.TryGetProperty("name", out var designerName)
                    ? designerName.GetString()
                    : null;
            var creatorId = item.TryGetProperty("designer", out var d2)
                && d2.TryGetProperty("id", out var did)
                    ? did.ToString()
                    : null;

            models.Add(new ScrapedModel
            {
                ExternalId = id,
                Name = name,
                CreatorName = creatorName,
                CreatorId = creatorId,
                Type = item.TryGetProperty("type", out var t) ? t.GetString() : null,
            });
        }

        return models;
    }

    private async Task<IReadOnlyList<ScrapedModel>> FetchLibraryViaApiAsync(PluginContext context, string accessToken, CancellationToken ct)
    {
        var models = new List<ScrapedModel>();
        var delayMs = GetDelayMs(context);
        int page = 1;
        bool hasMore = true;

        context.HttpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        while (hasMore && !ct.IsCancellationRequested)
        {
            try
            {
                var response = await context.HttpClient.GetAsync(
                    $"{MmfApiBase}/users/me/objects?page={page}&per_page=50", ct);

                if (!response.IsSuccessStatusCode) break;

                var result = await response.Content.ReadFromJsonAsync<MmfApiListResponse>(JsonOptions, ct);
                if (result?.Items is null || result.Items.Count == 0) break;

                foreach (var item in result.Items)
                {
                    models.Add(new ScrapedModel
                    {
                        ExternalId = item.Id.ToString(),
                        Name = item.Name ?? $"Model {item.Id}",
                        CreatorName = item.Designer?.Name,
                        CreatorId = item.Designer?.Id?.ToString(),
                        UpdatedAt = item.UpdatedAt,
                        Type = item.Type,
                    });
                }

                context.Progress.Report(new ScrapeProgress
                {
                    Status = "fetching_manifest",
                    Current = models.Count,
                    Total = result.TotalCount,
                    CurrentItem = $"Fetched page {page}...",
                });

                hasMore = models.Count < result.TotalCount;
                page++;
                await Task.Delay(delayMs, ct);
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning("Error fetching library page {Page}: {Error}", page, ex.Message);
                break;
            }
        }

        return models;
    }

    private static Dictionary<string, object?> BuildMetadata(ScrapedModel model, MmfModelDetails? details, List<DownloadedFile> files)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["metadataVersion"] = 2,
            ["source"] = "mmf",
            ["externalId"] = model.ExternalId,
            ["externalUrl"] = details?.Url ?? $"https://www.myminifactory.com/object/{model.ExternalId}",
            ["name"] = details?.Name ?? model.Name,
            ["description"] = details?.Description,
            ["type"] = details?.Type ?? model.Type,
            ["tags"] = details?.Tags?.Select(t => t.Name).Where(n => n != null).ToList() ?? new List<string?>(),
            ["creator"] = new Dictionary<string, object?>
            {
                ["externalId"] = model.CreatorId ?? details?.Designer?.Id?.ToString(),
                ["username"] = details?.Designer?.Username ?? model.CreatorName,
                ["displayName"] = details?.Designer?.Name ?? model.CreatorName,
                ["profileUrl"] = details?.Designer?.ProfileUrl,
            },
            ["dates"] = new Dictionary<string, object?>
            {
                ["created"] = details?.CreatedAt,
                ["updated"] = details?.UpdatedAt ?? model.UpdatedAt,
                ["published"] = details?.PublishedAt,
            },
            ["files"] = files.Select(f => new Dictionary<string, object?>
            {
                ["filename"] = f.Filename,
                ["localPath"] = Path.GetFileName(f.LocalPath),
                ["size"] = f.Size,
                ["variant"] = f.Variant,
            }).ToList(),
        };

        if (details?.Images is { Count: > 0 })
        {
            metadata["images"] = details.Images.Select(i => new Dictionary<string, object?>
            {
                ["url"] = i.Url ?? i.Original,
                ["type"] = "gallery",
            }).ToList();
        }

        return metadata;
    }

    private static string? DetectVariant(string? filename)
    {
        if (string.IsNullOrEmpty(filename)) return null;
        var lower = filename.ToLowerInvariant();

        if (lower.Contains("supported") && !lower.Contains("unsupported"))
            return "supported";
        if (lower.Contains("unsupported") || lower.Contains("no_support") || lower.Contains("nosupport"))
            return "unsupported";
        if (lower.Contains("presupported") || lower.Contains("pre-supported") || lower.Contains("pre_supported"))
            return "presupported";
        if (lower.Contains("lychee") || lower.Contains(".lys"))
            return "lychee";
        if (lower.Contains("chitubox") || lower.Contains(".ctb"))
            return "chitubox";

        return null;
    }

    private static bool IsArchiveFile(string? filename)
    {
        if (string.IsNullOrEmpty(filename)) return false;
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        return ext is ".zip" or ".rar" or ".7z" or ".tar" or ".gz";
    }

    private static int GetDelayMs(PluginContext context)
    {
        if (context.Config.TryGetValue("DELAY_MS", out var val) && int.TryParse(val, out var ms))
            return Math.Max(100, ms);
        return 1000;
    }

    private static string SanitizeFilename(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }

    private static string GetCallbackUrl(PluginContext context)
    {
        // Plugin host routes callbacks to /auth/{slug}/callback
        // The actual URL depends on deployment — use config or default
        return context.Config.TryGetValue("CALLBACK_URL", out var url) && !string.IsNullOrEmpty(url)
            ? url
            : "http://localhost:5000/auth/mmf/callback";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

// --- MMF API response models (internal to plugin) ---

internal class MmfApiListResponse
{
    [JsonPropertyName("items")]
    public List<MmfModelSummary>? Items { get; set; }

    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }
}

internal class MmfModelSummary
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    [JsonPropertyName("designer")]
    public MmfDesigner? Designer { get; set; }
}

internal class MmfModelDetails
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    [JsonPropertyName("published_at")]
    public DateTime? PublishedAt { get; set; }

    [JsonPropertyName("designer")]
    public MmfDesigner? Designer { get; set; }

    [JsonPropertyName("images")]
    public List<MmfImage>? Images { get; set; }

    [JsonPropertyName("files")]
    public List<MmfFile>? Files { get; set; }

    [JsonPropertyName("tags")]
    public List<MmfTag>? Tags { get; set; }
}

internal class MmfDesigner
{
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("profile_url")]
    public string? ProfileUrl { get; set; }
}

internal class MmfImage
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("original")]
    public string? Original { get; set; }
}

internal class MmfFile
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("download_url")]
    public string? DownloadUrl { get; set; }
}

internal class MmfTag
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
