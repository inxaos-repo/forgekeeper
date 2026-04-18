using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Forgekeeper.PluginSdk;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Forgekeeper.Scraper.Mmf;

/// <summary>
/// MyMiniFactory library scraper plugin.
/// 
/// Authentication: FlareSolverr for Cloudflare bypass + cookie-based login.
///                 Falls back to Bearer token from MiniDownloader download_token.
/// Manifest: Fetched via FlareSolverr session cookies (data-library API).
///           Also supports manual JSON upload via the Plugins page.
/// Scraping: Uses MMF v2 API for model details and file downloads.
///           403 fallback chain: Bearer → session cookies → Playwright headless browser.
/// </summary>
public class MmfScraperPlugin : ILibraryScraper, IAsyncDisposable
{
    // Playwright browser — lazily created on first 403 fallback, shared across models in a sync
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _browserContext;

    // Background unzip worker
    private readonly ConcurrentQueue<(string archivePath, string extractDir, string filename)> _unzipQueue = new();
    private volatile bool _unzipRunning;
    private Task? _unzipTask;

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
        // CLIENT_ID is declared for config completeness; not used in the FlareSolverr login flow.
        // Kept in schema so existing plugin configs remain valid.
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
            Key = "SKIP_CREATORS",
            Label = "Skip Creators",
            Type = PluginConfigFieldType.String,
            Required = false,
            HelpText = "Comma-separated list of creator names to skip during sync.",
        },
        new PluginConfigField
        {
            Key = "RESTORE_MODE",
            Label = "Restore Mode",
            Type = PluginConfigFieldType.String,
            Required = false,
            DefaultValue = "false",
            HelpText = "Set to 'true' to enable restore mode. Skips the lastSynced timestamp check so all models are re-scraped. Files already on disk are still skipped (gap-only restore).",
        },
        new PluginConfigField
        {
            Key = "DOWNLOAD_DELAY_MS",
            Label = "Download Delay (ms)",
            Type = PluginConfigFieldType.Number,
            Required = false,
            DefaultValue = "5000",
            HelpText = "Delay between file downloads in milliseconds. Increase to avoid rate-limiting during large restores. Default 5000ms.",
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
        // Legacy OAuth implicit flow: access_token is returned as a URL fragment parameter.
        // The primary auth flow is now FlareSolverr + cookie login (see FetchLibraryViaBrowserAsync).
        // This callback path is retained for future OAuth support or direct token injection.
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
        // Start background unzip worker
        _unzipRunning = true;
        _unzipTask = Task.Run(() => UnzipWorkerLoop(context.Logger, ct), ct);

        context.Progress.Report(new ScrapeProgress
        {
            Status = "fetching_manifest",
            CurrentItem = "Loading library manifest...",
        });

        // If user uploaded a manifest JSON, use it directly (secondary path)
        if (uploadedManifest is not null)
        {
            return await ParseUploadedManifestAsync(uploadedManifest, ct);
        }

        // Primary path: FlareSolverr login + HttpClient library fetch
        // Uses FlareSolverr to solve Cloudflare challenge, logs in, then fetches
        // the data-library API with session cookies via a plain HttpClient.
        return await FetchLibraryViaBrowserAsync(context, ct);
    }

    public async Task<ScrapeResult> ScrapeModelAsync(PluginContext context, ScrapedModel model, CancellationToken ct = default)
    {
        var modelDir = context.ModelDirectory
            ?? throw new InvalidOperationException("ModelDirectory not set in context");

        var delayMs = GetDelayMs(context);
        var downloadDelayMs = GetDownloadDelayMs(context);
        var restoreMode = IsRestoreMode(context);
        
        // Get auth tokens — try download_token (MiniDownloader) or access_token (OAuth)
        var bearerToken = await context.TokenStore.GetTokenAsync("download_token", ct)
            ?? await context.TokenStore.GetTokenAsync("access_token", ct);

        // FlareSolverr session cookies — used as fallback when Bearer returns 403
        var sessionCookies = await context.TokenStore.GetTokenAsync("session_cookies", ct);
        var sessionUA = await context.TokenStore.GetTokenAsync("session_useragent", ct);

        // Strip 'object-' prefix from external ID (manifest stores 'object-12345' but API wants '12345')
        var numericId = model.ExternalId?.StartsWith("object-") == true 
            ? model.ExternalId[7..] 
            : model.ExternalId;

        context.Progress.Report(new ScrapeProgress
        {
            Status = "downloading",
            CurrentItem = model.Name,
        });

        int filesDownloaded = 0, filesSkipped = 0, filesFailed = 0;

        try
        {
            // ── Step 1: Fetch model details from v2 API ──
            MmfModelDetails? details = null;
            string? archiveDownloadUrl = null;

            if (!string.IsNullOrEmpty(bearerToken))
            {
                using var apiClient = CreateApiClient(bearerToken);

                var response = await apiClient.GetAsync($"/api/v2/objects/{numericId}", ct);
                if (response.IsSuccessStatusCode)
                {
                    var objJson = await response.Content.ReadAsStringAsync(ct);
                    // HTML response guard (CF error pages, etc.)
                    if (objJson.TrimStart().StartsWith("<"))
                    {
                        context.Logger.LogWarning("[MMF] Got HTML response for model {Id} — CF block?", model.ExternalId);
                        return ScrapeResult.Failure($"HTML response for {model.Name} (possible CF block)");
                    }
                    // Parse everything manually — MMF API types are wildly inconsistent
                    using var objDoc = JsonDocument.Parse(objJson);
                    var root = objDoc.RootElement;
                    details = ParseModelDetails(root);

                    if (root.TryGetProperty("archive_download_url", out var archUrl) && archUrl.ValueKind == JsonValueKind.String)
                        archiveDownloadUrl = archUrl.GetString();

                    // Try inline files
                    if (root.TryGetProperty("files", out var filesNode) && filesNode.ValueKind == JsonValueKind.Object)
                    {
                        if (filesNode.TryGetProperty("items", out var inlineItems) && inlineItems.ValueKind == JsonValueKind.Array)
                            details.Files = ParseFiles(inlineItems);
                    }
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    context.Logger.LogDebug("[MMF] 404 for model {Id} ({Name}) — skipping", model.ExternalId, model.Name);
                    return ScrapeResult.Ok("metadata.json", []); // 404 = skip, still write metadata
                }
                else
                {
                    context.Logger.LogDebug("[MMF] API {Status} for model {Id} ({Name})", response.StatusCode, model.ExternalId, model.Name);
                }
                await Task.Delay(delayMs, ct);

                // ── Step 2: Fetch file download URLs (if inline was empty) ──
                if (details != null && (details.Files == null || details.Files.Count == 0))
                {
                    var filesResponse = await apiClient.GetAsync($"/api/v2/objects/{numericId}/files?per_page=100", ct);
                    if (filesResponse.IsSuccessStatusCode)
                    {
                        var filesJson = await filesResponse.Content.ReadAsStringAsync(ct);
                        if (!filesJson.TrimStart().StartsWith("<")) // HTML guard
                        {
                            using var filesDoc = JsonDocument.Parse(filesJson);
                            if (filesDoc.RootElement.TryGetProperty("items", out var fileItems)
                                && fileItems.ValueKind == JsonValueKind.Array)
                            {
                                details.Files = ParseFiles(fileItems);
                            }
                        }
                    }
                    await Task.Delay(delayMs, ct);
                }

                // ── Step 3: Fallback to archive_download_url ──
                if (details != null && (details.Files == null || details.Files.Count == 0) && !string.IsNullOrEmpty(archiveDownloadUrl))
                {
                    var archiveName = $"{SanitizeFilename(model.Name)}.zip";
                    details.Files = [new MmfFile { Filename = archiveName, DownloadUrl = archiveDownloadUrl, Size = 0 }];
                    context.Logger.LogDebug("[MMF] Using archive_download_url for {Name}", model.Name);
                }
            }
            else
            {
                context.Logger.LogWarning("[MMF] No bearer token — skipping API details for {Model}", model.Name);
            }

            // ── Step 4: Download files ──
            var downloadedFiles = new List<DownloadedFile>();
            if (details?.Files is { Count: > 0 })
            {
                foreach (var file in details.Files)
                {
                    if (ct.IsCancellationRequested) break;
                    if (string.IsNullOrEmpty(file.DownloadUrl)) continue;

                    var safeName = SanitizeFilename(file.Filename);
                    var filePath = Path.Combine(modelDir, safeName);
                    Directory.CreateDirectory(modelDir);

                    // Skip if already downloaded (check size, or fuzzy search subdirs)
                    var existingPath = FindExistingFile(modelDir, safeName, file.Size);
                    if (existingPath != null)
                    {
                        downloadedFiles.Add(new DownloadedFile
                        {
                            Filename = file.Filename ?? safeName,
                            LocalPath = existingPath,
                            Size = new FileInfo(existingPath).Length,
                            Variant = DetectVariant(file.Filename),
                            IsArchive = IsArchiveFile(file.Filename),
                        });
                        filesSkipped++;
                        continue;
                    }

                    // Retry wrapper: max 3 attempts with exponential backoff (2s, 8s, 30s)
                    bool downloaded = false;
                    Exception? lastDownloadEx = null;
                    int[] retryBackoffSeconds = [2, 8, 30];

                    for (int attempt = 0; attempt < 3 && !downloaded && !ct.IsCancellationRequested; attempt++)
                    {
                        if (attempt > 0)
                        {
                            var waitSec = retryBackoffSeconds[attempt - 1];
                            context.Logger.LogWarning(
                                "[MMF] Retry attempt {Attempt}/3 for {File}, waiting {Wait}s",
                                attempt + 1, safeName, waitSec);
                            await Task.Delay(TimeSpan.FromSeconds(waitSec), ct);
                        }

                        try
                        {
                            context.Logger.LogInformation("[MMF] Downloading: {File} for {Model} (attempt {Attempt}/3)",
                                safeName, model.Name, attempt + 1);
                            using var dlClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                            // Use Bearer for MMF URLs, skip for CDN URLs
                            if (!string.IsNullOrEmpty(bearerToken) && !IsCdnUrl(file.DownloadUrl))
                                dlClient.DefaultRequestHeaders.Authorization =
                                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
                            dlClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

                            var fileResponse = await dlClient.GetAsync(file.DownloadUrl, ct);

                            // Check for 404 — don't retry, file doesn't exist
                            if (fileResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                            {
                                context.Logger.LogWarning("[MMF] 404 for {File} — skipping (file not found)", safeName);
                                filesFailed++;
                                break;
                            }

                            // Check for 429 — respect Retry-After header, then retry
                            if (fileResponse.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                            {
                                var retryAfter = fileResponse.Headers.RetryAfter?.Delta;
                                var rateLimitWait = retryAfter.HasValue
                                    ? (int)Math.Ceiling(retryAfter.Value.TotalSeconds)
                                    : retryBackoffSeconds[Math.Min(attempt, retryBackoffSeconds.Length - 1)];
                                context.Logger.LogWarning(
                                    "[MMF] 429 Too Many Requests for {File} — waiting {Wait}s (attempt {Attempt}/3)",
                                    safeName, rateLimitWait, attempt + 1);
                                await Task.Delay(TimeSpan.FromSeconds(rateLimitWait), ct);
                                lastDownloadEx = new HttpRequestException($"429 Too Many Requests for {safeName}",
                                    null, fileResponse.StatusCode);
                                continue; // next attempt
                            }

                            // Check for 5xx server errors — retry
                            if ((int)fileResponse.StatusCode >= 500)
                            {
                                lastDownloadEx = new HttpRequestException(
                                    $"Server error {(int)fileResponse.StatusCode} for {safeName}",
                                    null, fileResponse.StatusCode);
                                context.Logger.LogWarning(
                                    "[MMF] Server error {Status} for {File} (attempt {Attempt}/3)",
                                    (int)fileResponse.StatusCode, safeName, attempt + 1);
                                continue; // next attempt
                            }

                            // 403 fallback chain: Bearer → session cookies → Playwright browser
                            HttpClient? cookieDlClient = null;
                            bool downloadedByBrowser = false;
                            try
                            {
                                // Fallback 1: retry with FlareSolverr session cookies
                                if (fileResponse.StatusCode == System.Net.HttpStatusCode.Forbidden
                                    && !string.IsNullOrEmpty(sessionCookies))
                                {
                                    context.Logger.LogWarning(
                                        "[MMF] 403 on Bearer download — retrying with session cookies for {File}", safeName);
                                    fileResponse.Dispose();
                                    cookieDlClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                                    cookieDlClient.DefaultRequestHeaders.Add("Cookie", sessionCookies);
                                    cookieDlClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                                        sessionUA ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                                    fileResponse = await cookieDlClient.GetAsync(file.DownloadUrl, ct);
                                }

                                // Fallback 2: Playwright headless browser (Cloudflare bypass)
                                if (fileResponse.StatusCode == System.Net.HttpStatusCode.Forbidden
                                    && !string.IsNullOrEmpty(bearerToken))
                                {
                                    context.Logger.LogWarning(
                                        "[MMF] 403 on cookie download — retrying with Playwright browser for {File}", safeName);
                                    fileResponse.Dispose();
                                    cookieDlClient?.Dispose();
                                    cookieDlClient = null;
                                    downloadedByBrowser = await DownloadWithBrowserAsync(
                                        file.DownloadUrl, filePath, bearerToken, context.Logger, ct);
                                    if (!downloadedByBrowser)
                                        throw new Exception($"All download methods (Bearer, cookies, Playwright) returned 403 for {safeName}");
                                }

                                if (!downloadedByBrowser)
                                {
                                    fileResponse.EnsureSuccessStatusCode();
                                    await using var fs = File.Create(filePath);
                                    await fileResponse.Content.CopyToAsync(fs, ct);
                                }
                            }
                            finally
                            {
                                cookieDlClient?.Dispose();
                            }

                            var actualSize = new FileInfo(filePath).Length;
                            var variant = DetectVariant(file.Filename);
                            downloadedFiles.Add(new DownloadedFile
                            {
                                Filename = file.Filename ?? safeName,
                                LocalPath = filePath,
                                Size = actualSize,
                                Variant = variant,
                                IsArchive = IsArchiveFile(file.Filename),
                            });
                            filesDownloaded++;
                            downloaded = true;
                        }
                        catch (TaskCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw; // Propagate real cancellation
                        }
                        catch (TaskCanceledException ex)
                        {
                            lastDownloadEx = ex;
                            context.Logger.LogWarning(
                                "[MMF] Timeout on attempt {Attempt}/3 for {File}", attempt + 1, safeName);
                        }
                        catch (HttpRequestException ex) when (
                            ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                            ((int?)ex.StatusCode >= 500))
                        {
                            lastDownloadEx = ex;
                            context.Logger.LogWarning(
                                "[MMF] Retryable HTTP error {Status} on attempt {Attempt}/3 for {File}",
                                ex.StatusCode, attempt + 1, safeName);
                        }
                        catch (Exception ex)
                        {
                            // Non-retryable error (403 exhausted, general exception)
                            lastDownloadEx = ex;
                            context.Logger.LogWarning(
                                "[MMF] Non-retryable error on attempt {Attempt}/3 for {File}: {Error}",
                                attempt + 1, safeName, ex.Message);
                            break;
                        }
                    }

                    if (!downloaded && lastDownloadEx != null)
                    {
                        context.Logger.LogWarning("[MMF] Failed to download {File} after all attempts: {Error}",
                            safeName, lastDownloadEx.Message);
                        filesFailed++;
                    }

                    // Use download-specific delay (configurable, default 5s) for inter-file gaps
                    await Task.Delay(downloadDelayMs, ct);
                }
            }

            // ── Step 5: Queue archives for background extraction ──
            foreach (var dl in downloadedFiles.Where(f => f.IsArchive && File.Exists(f.LocalPath)))
            {
                var extractDir = Path.Combine(
                    Path.GetDirectoryName(dl.LocalPath)!,
                    Path.GetFileNameWithoutExtension(dl.LocalPath));
                _unzipQueue.Enqueue((dl.LocalPath, extractDir, dl.Filename ?? Path.GetFileName(dl.LocalPath)));
            }

            // Also queue leftover ZIPs from previous interrupted syncs
            try
            {
                foreach (var zip in Directory.EnumerateFiles(modelDir, "*.zip").Concat(
                                    Directory.EnumerateFiles(modelDir, "*.rar")).Concat(
                                    Directory.EnumerateFiles(modelDir, "*.7z")))
                {
                    var extractTo = Path.Combine(modelDir, Path.GetFileNameWithoutExtension(zip));
                    _unzipQueue.Enqueue((zip, extractTo, Path.GetFileName(zip)));
                }
            }
            catch { }

            // ── Step 6: Load existing metadata + write updated metadata.json ──
            Dictionary<string, object?>? existingMetadata = null;
            var metadataPath = Path.Combine(modelDir, "metadata.json");
            if (File.Exists(metadataPath))
            {
                try
                {
                    var existingJson = await File.ReadAllTextAsync(metadataPath, ct);
                    existingMetadata = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                        existingJson, JsonOptions);
                }
                catch { /* If we can't read existing, start fresh */ }
            }

            // Check if source has been updated since last sync
            // In restore mode, skip this check so all models are processed (gap-only restore via FindExistingFile)
            if (!restoreMode && existingMetadata != null && details?.UpdatedAt != null)
            {
                var lastSynced = GetDateFromMetadata(existingMetadata, "lastSynced");
                var sourceUpdated = details.UpdatedAt;
                if (lastSynced.HasValue && sourceUpdated <= lastSynced && filesDownloaded == 0)
                {
                    // Source hasn't changed and no new files — skip metadata rewrite
                    filesSkipped++;
                    return ScrapeResult.Ok("metadata.json", downloadedFiles);
                }
            }

            var metadata = BuildMetadata(model, details, downloadedFiles, existingMetadata);
            var json = JsonSerializer.Serialize(metadata, JsonWriteOptions);
            await File.WriteAllTextAsync(metadataPath, json, ct);

            if (filesDownloaded > 0)
                context.Logger.LogInformation("[MMF] {Model}: {Downloaded} downloaded, {Skipped} skipped, {Failed} failed", 
                    model.Name, filesDownloaded, filesSkipped, filesFailed);

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

    // --- Playwright browser download helpers ---

    private async Task<IBrowserContext> GetBrowserContextAsync(string bearerToken, ILogger logger)
    {
        if (_browserContext != null) return _browserContext;

        logger.LogInformation("[MMF] Launching headless Chromium for 403 fallback downloads...");
        _playwright = await Playwright.CreateAsync();
        // Use system Chromium if available (Docker image), fall back to Playwright's bundled version
        var chromiumPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH");
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions 
        { 
            Headless = true,
            ExecutablePath = !string.IsNullOrEmpty(chromiumPath) ? chromiumPath : null,
        });
        _browserContext = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            ExtraHTTPHeaders = new Dictionary<string, string> { ["Accept-Language"] = "en-US,en;q=0.9" }
        });

        // Inject Authorization header on all MMF origin requests
        await _browserContext.RouteAsync("**/*myminifactory.com/**", async route =>
        {
            var headers = new Dictionary<string, string>(route.Request.Headers)
            {
                ["Authorization"] = $"Bearer {bearerToken}"
            };
            await route.ContinueAsync(new RouteContinueOptions { Headers = headers });
        });

        return _browserContext;
    }

    private async Task<bool> DownloadWithBrowserAsync(
        string url, string filePath, string bearerToken, ILogger logger, CancellationToken ct)
    {
        try
        {
            var context = await GetBrowserContextAsync(bearerToken, logger);
            var page = await context.NewPageAsync();
            try
            {
                string? cdnUrl = null;

                // Capture CDN redirect URL from response events
                page.Response += (_, resp) =>
                {
                    if (resp.Url.Contains("dl4.myminifactory.com") ||
                        resp.Url.Contains("dl3.myminifactory.com") ||
                        resp.Url.Contains("dl.myminifactory.com") ||
                        resp.Url.Contains("cdn.myminifactory.com"))
                    {
                        cdnUrl = resp.Url;
                    }
                };

                try
                {
                    await page.GotoAsync(url, new PageGotoOptions
                    {
                        Timeout = 30000,
                        WaitUntil = WaitUntilState.Commit
                    });
                }
                catch { /* Expected — CDN redirect typically aborts navigation */ }

                await Task.Delay(2000, ct);

                if (cdnUrl != null)
                {
                    logger.LogDebug("[MMF] Browser CDN redirect: {Url}",
                        cdnUrl.Length > 100 ? cdnUrl[..100] : cdnUrl);

                    using var dlClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                    dlClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

                    var cdnResponse = await dlClient.GetAsync(
                        cdnUrl, HttpCompletionOption.ResponseHeadersRead, ct);

                    if (cdnResponse.IsSuccessStatusCode)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                        await using var output = File.Create(filePath);
                        await using var stream = await cdnResponse.Content.ReadAsStreamAsync(ct);
                        await stream.CopyToAsync(output, ct);
                        logger.LogInformation("[MMF] Browser download succeeded: {File}", Path.GetFileName(filePath));
                        return true;
                    }

                    logger.LogWarning("[MMF] Browser CDN download failed: {Status}", cdnResponse.StatusCode);
                }
                else
                {
                    logger.LogWarning("[MMF] Playwright did not capture CDN URL for {Url}", url);
                }
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("[MMF] Playwright browser download failed: {Error}", ex.Message);
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browserContext != null)
        {
            try { await _browserContext.DisposeAsync(); } catch { }
            _browserContext = null;
        }
        if (_browser != null)
        {
            try { await _browser.DisposeAsync(); } catch { }
            _browser = null;
        }
        if (_playwright != null)
        {
            try { _playwright.Dispose(); } catch { }
            _playwright = null;
        }

        // Stop unzip worker and wait for it to drain
        _unzipRunning = false;
        if (_unzipTask != null)
        {
            try { await Task.WhenAny(_unzipTask, Task.Delay(TimeSpan.FromSeconds(60))); } catch { }
        }
    }

    // --- Background Unzip Worker ---

    private async Task UnzipWorkerLoop(ILogger logger, CancellationToken ct)
    {
        int extracted = 0;
        try
        {
            while (_unzipRunning || !_unzipQueue.IsEmpty)
            {
                if (ct.IsCancellationRequested) break;

                if (_unzipQueue.TryDequeue(out var job))
                {
                    var (archivePath, extractDir, filename) = job;
                    try
                    {
                        if (!File.Exists(archivePath)) continue;
                        var ext = Path.GetExtension(archivePath).ToLowerInvariant();

                        // Check if already extracted and current
                        if (Directory.Exists(extractDir) &&
                            Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories).Any())
                        {
                            bool needsUpdate = false;
                            if (ext == ".zip")
                            {
                                try
                                {
                                    using var archive = ZipFile.OpenRead(archivePath);
                                    var newestLocal = Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories)
                                        .Select(f => File.GetLastWriteTimeUtc(f)).Max();
                                    needsUpdate = archive.Entries.Any(e => e.Length > 0 && e.LastWriteTime.UtcDateTime > newestLocal);
                                }
                                catch { needsUpdate = true; }
                            }
                            else { needsUpdate = true; } // For RAR/7z, always re-extract if queued

                            if (!needsUpdate)
                            {
                                logger.LogDebug("[UNZIP] Already current, cleaning up: {File}", filename);
                                try { File.Delete(archivePath); } catch { }
                                continue;
                            }
                        }

                        Directory.CreateDirectory(extractDir);

                        if (ext == ".zip")
                        {
                            ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true);
                            extracted++;
                            logger.LogInformation("[MMF] Extracted: {File}", filename);
                        }
                        else if (ext is ".rar" or ".7z")
                        {
                            // Shell out to 7z if available
                            var sevenZip = FindExecutable("7z") ?? FindExecutable("7za") ?? FindExecutable("p7zip");
                            if (sevenZip != null)
                            {
                                var psi = new ProcessStartInfo(sevenZip, $"x \"{archivePath}\" -o\"{extractDir}\" -y")
                                {
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true,
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                };
                                using var proc = Process.Start(psi);
                                if (proc != null)
                                {
                                    await proc.WaitForExitAsync(ct);
                                    if (proc.ExitCode == 0)
                                    {
                                        extracted++;
                                        logger.LogInformation("[MMF] Extracted ({Ext}): {File}", ext, filename);
                                    }
                                    else
                                    {
                                        var stderr = await proc.StandardError.ReadToEndAsync(ct);
                                        logger.LogWarning("[UNZIP] 7z failed for {File}: {Error}", filename, stderr.Trim());
                                        continue; // Don't delete archive if extraction failed
                                    }
                                }
                            }
                            else
                            {
                                logger.LogWarning("[UNZIP] {Ext} not supported (install p7zip-full): {File}", ext, filename);
                                continue; // Don't delete — can't extract
                            }
                        }
                        else
                        {
                            logger.LogWarning("[UNZIP] Unknown archive format: {File}", filename);
                            continue;
                        }

                        // Clean up old version directories
                        CleanupOldVersions(extractDir, logger);

                        // Delete archive after successful extraction
                        try { File.Delete(archivePath); }
                        catch (Exception delEx) { logger.LogWarning("[UNZIP] Could not delete archive: {Error}", delEx.Message); }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning("[UNZIP] Error extracting {File}: {Error}", filename, ex.Message);
                    }
                }
                else
                {
                    await Task.Delay(500, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError("[UNZIP] Worker crashed: {Error}", ex.Message);
        }

        if (extracted > 0)
            logger.LogInformation("[UNZIP] Background worker extracted {Count} archive(s)", extracted);
    }

    /// <summary>Remove old version directories (e.g., "Model v1" when "Model v2" exists).</summary>
    internal static void CleanupOldVersions(string extractDir, ILogger logger)
    {
        try
        {
            var parent = Path.GetDirectoryName(extractDir);
            if (parent == null) return;

            var baseName = Path.GetFileName(extractDir);
            // Strip version suffixes: "Model v2", "Model_v1.0", "Model MKIV"
            var versionPattern = System.Text.RegularExpressions.Regex.Replace(
                baseName, @"[\s_-]*(v\d+(\.\d+)*|mk\w+)$", "", 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (versionPattern == baseName) return; // No version suffix found

            foreach (var dir in Directory.GetDirectories(parent))
            {
                var dirName = Path.GetFileName(dir);
                if (dirName == baseName) continue; // Skip self

                var otherBase = System.Text.RegularExpressions.Regex.Replace(
                    dirName, @"[\s_-]*(v\d+(\.\d+)*|mk\w+)$", "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (otherBase.Equals(versionPattern, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("[UNZIP] Removing old version: {Old} (newer: {New})", dirName, baseName);
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
            }
        }
        catch { }
    }

    private static string? FindExecutable(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("which", name)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var result = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            return proc.ExitCode == 0 && !string.IsNullOrEmpty(result) ? result : null;
        }
        catch { return null; }
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
            var cfCookies = new List<(string Name, string Value)>();
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
                        cfCookies.Add((
                            Name: cookie.GetProperty("name").GetString() ?? "",
                            Value: cookie.GetProperty("value").GetString() ?? ""
                        ));
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
            
            // Save cookies + user agent for use in ScrapeModelAsync (file downloads)
            await context.TokenStore.SaveTokenAsync("session_cookies", cookieHeader, ct);
            await context.TokenStore.SaveTokenAsync("session_useragent", solvedUserAgent ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36", ct);
            context.Logger.LogInformation("[MMF] Saved session cookies for file downloads");

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

    /// <summary>Build metadata.json, preserving user edits from existing metadata.</summary>
    internal static Dictionary<string, object?> BuildMetadata(
        ScrapedModel model, MmfModelDetails? details, List<DownloadedFile> files,
        Dictionary<string, object?>? existing = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["metadataVersion"] = 3,
            ["source"] = "mmf",
            ["externalId"] = model.ExternalId,
            ["externalUrl"] = details?.Url ?? $"https://www.myminifactory.com/object/{model.ExternalId}",
            ["name"] = details?.Name ?? model.Name,
            ["description"] = details?.Description,
            ["type"] = details?.Type ?? model.Type,
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
                ["lastSynced"] = DateTime.UtcNow,
            },
            ["files"] = files.Select(f => new Dictionary<string, object?>
            {
                ["filename"] = f.Filename,
                ["localPath"] = Path.GetFileName(f.LocalPath),
                ["size"] = f.Size,
                ["variant"] = f.Variant,
            }).ToList(),
        };

        // Merge tags: source tags + existing user tags (deduplicated)
        var sourceTags = details?.Tags?.Select(t => t.Name).Where(n => n != null).ToList() ?? new List<string?>();
        var userTags = GetExistingList(existing, "userTags");
        var allTags = sourceTags.Concat(userTags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        metadata["tags"] = allTags;
        metadata["sourceTags"] = sourceTags; // Track which came from MMF
        metadata["userTags"] = userTags;     // Track user-added tags

        if (details?.Images is { Count: > 0 })
        {
            metadata["images"] = details.Images.Select(i => new Dictionary<string, object?>
            {
                ["url"] = i.Url ?? i.Original?.Url ?? i.Standard?.Url ?? i.Thumbnail?.Url,
                ["type"] = "gallery",
            }).ToList();
        }

        // Preserve user-edited fields from existing metadata
        if (existing != null)
        {
            // These fields are user-owned — never overwrite with source data
            PreserveField(metadata, existing, "rating");
            PreserveField(metadata, existing, "printStatus");
            PreserveField(metadata, existing, "printHistory");
            PreserveField(metadata, existing, "notes");
            PreserveField(metadata, existing, "scale");
            PreserveField(metadata, existing, "gameSystem");
            PreserveField(metadata, existing, "collection");
            PreserveField(metadata, existing, "components");
            PreserveField(metadata, existing, "license");

            // Preserve user description if they edited it
            if (existing.TryGetValue("userDescription", out var ud) && ud != null)
            {
                metadata["description"] = ud;
                metadata["userDescription"] = ud;
            }
        }

        return metadata;
    }

    /// <summary>Extract a DateTime from nested metadata (e.g., dates.lastSynced).</summary>
    private static DateTime? GetDateFromMetadata(Dictionary<string, object?> metadata, string dateKey)
    {
        if (metadata.TryGetValue("dates", out var datesVal) && datesVal is JsonElement datesEl 
            && datesEl.ValueKind == JsonValueKind.Object
            && datesEl.TryGetProperty(dateKey, out var dateProp)
            && dateProp.ValueKind == JsonValueKind.String
            && DateTime.TryParse(dateProp.GetString(), out var dt))
            return dt;
        return null;
    }

    /// <summary>Preserve a field from existing metadata if it has a value.</summary>
    private static void PreserveField(Dictionary<string, object?> target, Dictionary<string, object?> source, string key)
    {
        if (source.TryGetValue(key, out var value) && value != null)
            target[key] = value;
    }

    /// <summary>Get a list of strings from existing metadata.</summary>
    private static List<string?> GetExistingList(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata == null) return new List<string?>();
        if (!metadata.TryGetValue(key, out var val) || val == null) return new List<string?>();
        if (val is JsonElement je && je.ValueKind == JsonValueKind.Array)
            return je.EnumerateArray().Select(e => e.GetString()).ToList();
        if (val is List<string?> list) return list;
        if (val is List<object?> objList) return objList.Select(o => o?.ToString()).ToList();
        return new List<string?>();
    }

    internal static string? DetectVariant(string? filename)
    {
        if (string.IsNullOrEmpty(filename)) return null;
        var lower = filename.ToLowerInvariant();

        // Order matters: check presupported BEFORE supported (presupported contains "supported")
        if (lower.Contains("presupported") || lower.Contains("pre-supported") || lower.Contains("pre_supported"))
            return "presupported";
        if (lower.Contains("unsupported") || lower.Contains("no_support") || lower.Contains("nosupport"))
            return "unsupported";
        if (lower.Contains("supported"))
            return "supported";
        if (lower.Contains("lychee") || lower.Contains(".lys"))
            return "lychee";
        if (lower.Contains("chitubox") || lower.Contains(".ctb"))
            return "chitubox";

        return null;
    }

    internal static bool IsArchiveFile(string? filename)
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

    private static int GetDownloadDelayMs(PluginContext context)
    {
        if (context.Config.TryGetValue("DOWNLOAD_DELAY_MS", out var val) && int.TryParse(val, out var ms))
            return Math.Max(0, ms);
        return 5000;
    }

    private static bool IsRestoreMode(PluginContext context)
    {
        if (context.Config.TryGetValue("RESTORE_MODE", out var val))
            return val.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    internal static string SanitizeFilename(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }

    /// <summary>Parse model details from raw JSON (avoids typed deserialization issues with MMF's inconsistent API).</summary>
    internal static MmfModelDetails ParseModelDetails(JsonElement root)
    {
        var details = new MmfModelDetails
        {
            Id = root.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
            Name = root.TryGetProperty("name", out var name) ? name.GetString() : null,
            Url = root.TryGetProperty("url", out var url) ? url.GetString() : null,
            Description = root.TryGetProperty("description", out var desc) ? desc.GetString() : null,
            Type = root.TryGetProperty("type", out var type) ? type.GetString() : null,
        };

        if (root.TryGetProperty("created_at", out var ca) && ca.ValueKind == JsonValueKind.String
            && DateTime.TryParse(ca.GetString(), out var cdt)) details.CreatedAt = cdt;
        if (root.TryGetProperty("updated_at", out var ua) && ua.ValueKind == JsonValueKind.String
            && DateTime.TryParse(ua.GetString(), out var udt)) details.UpdatedAt = udt;
        if (root.TryGetProperty("published_at", out var pa) && pa.ValueKind == JsonValueKind.String
            && DateTime.TryParse(pa.GetString(), out var pdt)) details.PublishedAt = pdt;

        if (root.TryGetProperty("designer", out var designer) && designer.ValueKind == JsonValueKind.Object)
        {
            details.Designer = new MmfDesigner
            {
                Id = designer.TryGetProperty("id", out var did) && did.ValueKind == JsonValueKind.Number ? did.GetInt64() : null,
                Name = designer.TryGetProperty("name", out var dn) ? dn.GetString() : null,
                Username = designer.TryGetProperty("username", out var du) ? du.GetString() : null,
                ProfileUrl = designer.TryGetProperty("profile_url", out var dp) ? dp.GetString() : null,
            };
        }

        // Tags — can be strings OR objects with {name}
        if (root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            details.Tags = [];
            foreach (var tag in tags.EnumerateArray())
            {
                string? tagName = tag.ValueKind == JsonValueKind.String
                    ? tag.GetString()
                    : tag.TryGetProperty("name", out var tn) ? tn.GetString() : null;
                if (tagName != null)
                    details.Tags.Add(new MmfTag { Name = tagName });
            }
        }

        // Images — original/url can be string or object
        if (root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
        {
            details.Images = [];
            foreach (var img in images.EnumerateArray())
            {
                var mmfImg = new MmfImage();
                if (img.TryGetProperty("url", out var iurl))
                    mmfImg.Url = iurl.ValueKind == JsonValueKind.String ? iurl.GetString() : null;
                if (img.TryGetProperty("original", out var orig))
                {
                    if (orig.ValueKind == JsonValueKind.Object)
                        mmfImg.Original = new MmfImageVariant { Url = orig.TryGetProperty("url", out var ou) ? ou.GetString() : null };
                    else if (orig.ValueKind == JsonValueKind.String)
                        mmfImg.Original = new MmfImageVariant { Url = orig.GetString() };
                }
                if (img.TryGetProperty("standard", out var std) && std.ValueKind == JsonValueKind.Object)
                    mmfImg.Standard = new MmfImageVariant { Url = std.TryGetProperty("url", out var su) ? su.GetString() : null };
                if (img.TryGetProperty("thumbnail", out var thumb) && thumb.ValueKind == JsonValueKind.Object)
                    mmfImg.Thumbnail = new MmfImageVariant { Url = thumb.TryGetProperty("url", out var tu) ? tu.GetString() : null };
                details.Images.Add(mmfImg);
            }
        }

        return details;
    }

    /// <summary>Parse files from a JSON array (handles null/missing size, etc.).</summary>
    internal static List<MmfFile> ParseFiles(JsonElement itemsArray)
    {
        var files = new List<MmfFile>();
        foreach (var f in itemsArray.EnumerateArray())
        {
            var filename = f.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
            var downloadUrl = f.TryGetProperty("download_url", out var du) ? du.GetString() : null;
            long size = 0;
            if (f.TryGetProperty("size", out var sz))
            {
                if (sz.ValueKind == JsonValueKind.Number) size = sz.GetInt64();
                else if (sz.ValueKind == JsonValueKind.String) long.TryParse(sz.GetString(), out size);
            }
            files.Add(new MmfFile
            {
                Id = f.TryGetProperty("id", out var fid) && fid.ValueKind == JsonValueKind.Number ? fid.GetInt64() : 0,
                Filename = filename,
                Size = size,
                DownloadUrl = downloadUrl,
            });
        }
        return files;
    }

    /// <summary>Create a pre-configured HttpClient for MMF API calls with Bearer auth.</summary>
    private static HttpClient CreateApiClient(string bearerToken)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://www.myminifactory.com"),
            Timeout = TimeSpan.FromSeconds(120),
        };
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>Check if a URL is a CDN URL (skip Bearer auth for CDN downloads).</summary>
    internal static bool IsCdnUrl(string url)
    {
        return url.Contains("cdn.myminifactory.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("dl.myminifactory.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("dl4.myminifactory.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Find an existing file by exact path or fuzzy search in subdirectories.</summary>
    internal static string? FindExistingFile(string modelDir, string safeName, long expectedSize)
    {
        // Exact match
        var exactPath = Path.Combine(modelDir, safeName);
        if (File.Exists(exactPath))
        {
            // If size is 0 (unknown), trust the file exists
            if (expectedSize == 0 || new FileInfo(exactPath).Length == expectedSize)
                return exactPath;
        }

        // Search subdirectories (variant folders, extracted archives)
        if (!Directory.Exists(modelDir)) return null;
        try
        {
            foreach (var found in Directory.EnumerateFiles(modelDir, safeName, SearchOption.AllDirectories))
            {
                if (expectedSize == 0 || new FileInfo(found).Length == expectedSize)
                    return found;
            }
        }
        catch { }

        return null;
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

    // NOTE: 'files' is NOT deserialized from JSON — the API returns it as {total_count, items: [...]}
    // which can't map to List<MmfFile>. We parse it manually from the raw JSON.
    [JsonIgnore]
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
    public MmfImageVariant? Original { get; set; }

    [JsonPropertyName("tiny")]
    public MmfImageVariant? Tiny { get; set; }

    [JsonPropertyName("thumbnail")]
    public MmfImageVariant? Thumbnail { get; set; }

    [JsonPropertyName("standard")]
    public MmfImageVariant? Standard { get; set; }
}

internal class MmfImageVariant
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }
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
// Build: 1776450164-playwright
