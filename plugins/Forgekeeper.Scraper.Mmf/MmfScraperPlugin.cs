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
    public bool RequiresBrowserAuth => false;

    public IReadOnlyList<PluginConfigField> ConfigSchema =>
    [
        new PluginConfigField
        {
            Key = "MMF_USERNAME",
            Label = "MyMiniFactory Email",
            Type = PluginConfigFieldType.String,
            Required = true,
            HelpText = "Your MyMiniFactory login email address.",
        },
        new PluginConfigField
        {
            Key = "MMF_PASSWORD",
            Label = "MyMiniFactory Password",
            Type = PluginConfigFieldType.Secret,
            Required = true,
            HelpText = "Your MyMiniFactory password. Stored encrypted.",
        },
        new PluginConfigField
        {
            Key = "CLIENT_ID",
            Label = "OAuth Client ID",
            Type = PluginConfigFieldType.String,
            Required = false,
            DefaultValue = "downloader_v2",
            HelpText = "MMF OAuth client ID. Default 'downloader_v2' works for most users.",
        },
        // CLIENT_SECRET not needed for OAuth implicit flow
        new PluginConfigField
        {
            Key = "FLARESOLVERR_URL",
            Label = "FlareSolverr URL",
            Type = PluginConfigFieldType.Url,
            Required = false,
            DefaultValue = "http://flaresolverr.flaresolverr.svc.cluster.local:8191",
            HelpText = "FlareSolverr URL for Cloudflare bypass. Leave blank to skip.",
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
        new PluginConfigField
        {
            Key = "CALLBACK_URL",
            Label = "OAuth Callback URL",
            Type = PluginConfigFieldType.Url,
            Required = false,
            DefaultValue = "https://forgekeeper.k8s.inxaos.com/auth/mmf/callback",
            HelpText = "The URL MMF redirects to after OAuth authorization. Must match your MMF app's registered redirect URI.",
        },
    ];

    public Task<AuthResult> AuthenticateAsync(PluginContext context, CancellationToken ct = default)
    {
        var username = context.Config.TryGetValue("MMF_USERNAME", out var u) ? u : null;
        var password = context.Config.TryGetValue("MMF_PASSWORD", out var p) ? p : null;

        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            return Task.FromResult(AuthResult.Success("Credentials configured — ready to sync"));
        }

        return Task.FromResult(AuthResult.Failed("MMF_USERNAME and MMF_PASSWORD must be configured in the plugin settings"));
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

        // If user uploaded a manifest JSON, use it directly
        if (uploadedManifest is not null)
        {
            return await ParseUploadedManifestAsync(uploadedManifest, ct);
        }

        // Try Playwright browser login with username/password
        return await FetchLibraryViaBrowserAsync(context, ct);
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
            <div class="plugin-admin" style="font-family: system-ui; max-width: 600px;">
                <h3>MyMiniFactory Plugin</h3>
                <p>To import your MMF library, run this script in your browser console while logged into MyMiniFactory:</p>
                <pre style="background: #1a1a2e; color: #0f0; padding: 12px; border-radius: 8px; font-family: monospace; font-size: 13px; margin: 12px 0;">fetch('/api/data-library/objectPreviews').then(r =&gt; r.json()).then(d =&gt; { copy(JSON.stringify(d)); console.log('Copied ' + d.length + ' objects!'); });</pre>
                <p>Then paste the JSON below and click Upload:</p>
                <form id="manifestForm" style="margin-top: 12px;">
                    <textarea id="manifestJson" rows="6" style="width: 100%; font-family: monospace; font-size: 12px; background: #111; color: #ddd; border: 1px solid #333; border-radius: 6px; padding: 8px;" placeholder="Paste your library JSON here..."></textarea>
                    <br/>
                    <p style="margin: 8px 0; color: #888;">Or upload a .json file:</p>
                    <input type="file" id="manifestFile" accept=".json" style="margin-bottom: 8px;" />
                    <br/>
                    <button type="submit" style="background: #f59e0b; color: #000; border: none; padding: 8px 24px; border-radius: 6px; cursor: pointer; font-weight: bold;">Upload Manifest</button>
                    <span id="manifestStatus" style="margin-left: 12px;"></span>
                </form>
                <script>
                document.getElementById('manifestForm').addEventListener('submit', async (e) => {
                    e.preventDefault();
                    const status = document.getElementById('manifestStatus');
                    status.textContent = 'Uploading...';
                    let body;
                    const jsonText = document.getElementById('manifestJson').value.trim();
                    const file = document.getElementById('manifestFile').files[0];
                    if (jsonText) {
                        body = jsonText;
                    } else if (file) {
                        body = await file.text();
                    } else {
                        status.textContent = 'Please paste JSON or select a file.';
                        return;
                    }
                    try {
                        const res = await fetch('/api/v1/plugins/mmf/manifest', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: body
                        });
                        const data = await res.json();
                        status.textContent = data.message || 'Done!';
                        status.style.color = res.ok ? '#4ade80' : '#f87171';
                    } catch (err) {
                        status.textContent = 'Error: ' + err.message;
                        status.style.color = '#f87171';
                    }
                });
                </script>
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

    private async Task<IReadOnlyList<ScrapedModel>> FetchLibraryViaBrowserAsync(PluginContext context, CancellationToken ct)
    {
        var username = context.Config.TryGetValue("MMF_USERNAME", out var u) ? u : null;
        var password = context.Config.TryGetValue("MMF_PASSWORD", out var p) ? p : null;

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            context.Logger.LogWarning("MMF_USERNAME and MMF_PASSWORD not configured");
            return [];
        }

        var flareSolverrUrl = context.Config.TryGetValue("FLARESOLVERR_URL", out var fs) ? fs : "http://flaresolverr.flaresolverr.svc.cluster.local:8191";

        context.Logger.LogInformation("[MMF] Starting library fetch via FlareSolverr + Playwright login...");

        try
        {
            // Step 1: Get CF cookies via FlareSolverr
            var cfCookies = new List<Cookie>();
            string? solvedUserAgent = null;

            if (!string.IsNullOrEmpty(flareSolverrUrl))
            {
                context.Logger.LogInformation("[MMF] Step 1: Login via FlareSolverr (CF bypass + session)...");
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
                
                // 1a: Create FlareSolverr session
                var createResp = await httpClient.PostAsync($"{flareSolverrUrl}/v1",
                    new StringContent(JsonSerializer.Serialize(new { cmd = "sessions.create" }), System.Text.Encoding.UTF8, "application/json"), ct);
                var createBody = await createResp.Content.ReadAsStringAsync(ct);
                context.Logger.LogInformation("[MMF] FlareSolverr create response: {Body}", createBody.Length > 200 ? createBody[..200] : createBody);
                var createJson = JsonDocument.Parse(createBody);
                if (!createJson.RootElement.TryGetProperty("session", out var sessionProp))
                {
                    context.Logger.LogError("[MMF] FlareSolverr failed to create session: {Body}", createBody[..Math.Min(500, createBody.Length)]);
                    return [];
                }
                var fsSession = sessionProp.GetString();
                context.Logger.LogInformation("[MMF] FlareSolverr session: {Session}", fsSession);

                // 1b: Get login page (solves CF + gets CSRF)
                var loginPageResp = await httpClient.PostAsync($"{flareSolverrUrl}/v1",
                    new StringContent(JsonSerializer.Serialize(new { cmd = "request.get", url = "https://www.myminifactory.com/login", session = fsSession, maxTimeout = 60000 }), System.Text.Encoding.UTF8, "application/json"), ct);
                var loginPageBody = await loginPageResp.Content.ReadAsStringAsync(ct);
                context.Logger.LogInformation("[MMF] Login page response length: {Len}", loginPageBody.Length);
                var loginPageJson = JsonDocument.Parse(loginPageBody);
                if (!loginPageJson.RootElement.TryGetProperty("solution", out var loginSol))
                {
                    context.Logger.LogError("[MMF] FlareSolverr login page failed: {Body}", loginPageBody[..Math.Min(500, loginPageBody.Length)]);
                    return [];
                }
                var loginHtml = loginSol.GetProperty("response").GetString() ?? "";
                
                // Extract CSRF token
                var csrfMatch = System.Text.RegularExpressions.Regex.Match(loginHtml, @"name=""_csrf_token""\s*value=""([^""]+)""");
                if (!csrfMatch.Success)
                {
                    context.Logger.LogError("[MMF] Could not find CSRF token on login page");
                    return [];
                }
                var csrfToken = csrfMatch.Groups[1].Value;
                context.Logger.LogInformation("[MMF] Got CSRF token");

                // 1c: POST login credentials
                var postData = $"_csrf_token={Uri.EscapeDataString(csrfToken)}&_username={Uri.EscapeDataString(username)}&_password={Uri.EscapeDataString(password)}&_remember_me=on&_submit=";
                var loginResp = await httpClient.PostAsync($"{flareSolverrUrl}/v1",
                    new StringContent(JsonSerializer.Serialize(new { cmd = "request.post", url = "https://www.myminifactory.com/login_check", session = fsSession, postData = postData, maxTimeout = 90000 }), System.Text.Encoding.UTF8, "application/json"), ct);
                var loginBody = await loginResp.Content.ReadAsStringAsync(ct);
                context.Logger.LogInformation("[MMF] Login POST response: {Body}", loginBody.Length > 300 ? loginBody[..300] : loginBody);
                var loginJson = JsonDocument.Parse(loginBody);
                if (!loginJson.RootElement.TryGetProperty("solution", out var loginSolution))
                {
                    context.Logger.LogError("[MMF] FlareSolverr login POST failed: {Body}", loginBody[..Math.Min(500, loginBody.Length)]);
                    return [];
                }
                var redirectUrl = loginSolution.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";
                
                // Extract user agent
                if (loginSolution.TryGetProperty("userAgent", out var uaProp))
                    solvedUserAgent = uaProp.GetString();

                // Extract ALL cookies (CF + session + REMEMBERME)
                if (loginSolution.TryGetProperty("cookies", out var cookiesArray))
                {
                    foreach (var cookie in cookiesArray.EnumerateArray())
                    {
                        cfCookies.Add(new Cookie
                        {
                            Name = cookie.GetProperty("name").GetString() ?? "",
                            Value = cookie.GetProperty("value").GetString() ?? "",
                            Domain = cookie.TryGetProperty("domain", out var d) ? d.GetString() ?? ".myminifactory.com" : ".myminifactory.com",
                            Path = cookie.TryGetProperty("path", out var pa) ? pa.GetString() ?? "/" : "/",
                        });
                    }
                }

                var hasRememberMe = cfCookies.Any(c => c.Name == "REMEMBERME");
                context.Logger.LogInformation("[MMF] Login {Result}: {Url}, cookies={Count}, REMEMBERME={HasRM}", 
                    hasRememberMe ? "SUCCESS" : "FAILED", redirectUrl, cfCookies.Count, hasRememberMe);
                
                // Cleanup FlareSolverr session
                try { await httpClient.PostAsync($"{flareSolverrUrl}/v1",
                    new StringContent(JsonSerializer.Serialize(new { cmd = "sessions.destroy", session = fsSession }), System.Text.Encoding.UTF8, "application/json"), ct); } catch {}
                
                if (!hasRememberMe)
                {
                    context.Logger.LogError("[MMF] Login did not produce REMEMBERME cookie — check credentials");
                    return [];
                }
            }

            // Step 2: Fetch data-library using HttpClient with FlareSolverr cookies
            // No Playwright needed — FlareSolverr already solved CF and logged in
            context.Logger.LogInformation("[MMF] Step 2: Fetching data-library with session cookies via HttpClient...");

            context.Progress.Report(new ScrapeProgress
            {
                Status = "fetching_manifest",
                CurrentItem = "Fetching library data with session cookies...",
            });

            // Build cookie header from FlareSolverr cookies
            var cookieHeader = string.Join("; ", cfCookies.Select(c => $"{c.Name}={c.Value}"));
            
            using var libraryClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            libraryClient.DefaultRequestHeaders.Add("Cookie", cookieHeader);
            libraryClient.DefaultRequestHeaders.Add("User-Agent", solvedUserAgent ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            var libraryResponse = await libraryClient.GetAsync("https://www.myminifactory.com/api/data-library/objectPreviews", ct);
            var jsonResult = await libraryResponse.Content.ReadAsStringAsync(ct);
            context.Logger.LogInformation("[MMF] Data-library response: status={Status}, length={Len}", libraryResponse.StatusCode, jsonResult.Length);

            if (string.IsNullOrEmpty(jsonResult) || jsonResult.Contains("\"error\""))
            {
                context.Logger.LogError("[MMF] Failed to fetch library: {Result}", jsonResult?.Substring(0, Math.Min(200, jsonResult?.Length ?? 0)));
                return [];
            }

            // Parse manifest
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(jsonResult));
            var models = await ParseUploadedManifestAsync(stream, ct);
            context.Logger.LogInformation("[MMF] Library manifest: {Count} models found!", models.Count);
            return models;
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "[MMF] Error during library fetch");
            return [];
        }
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
// Build: 1776450163
