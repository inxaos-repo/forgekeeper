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
/// Authentication: OAuth 2.0 implicit flow via auth.myminifactory.com/web/authorize
///                 (client_id=downloader_v2, response_type=token). The access_token
///                 is returned as a URL fragment, extracted by our callback HTML, and
///                 POSTed to /auth/mmf/callback. Token validity is verified via a live
///                 GET /api/v2/user ping on each AuthenticateAsync call.
/// Manifest: Primary — FlareSolverr CF bypass + Playwright headless login; manifest is
///           fetched INSIDE the Playwright page via page.EvaluateAsync(fetch(...)) so the
///           request carries Chromium's TLS fingerprint (CF's cf_clearance is bound to it).
///           HttpClient with the same cookies gets 403 HTML challenge (fingerprint mismatch).
///           Secondary — manual JSON upload via the Plugins page.
/// Scraping: MMF v2 API for model details and file download URLs.
///           Download: Bearer → 302 CDN redirect (no auth forwarded, AWS presigned URLs
///           reject extra Authorization headers). Fallback chain on 403:
///           [fallback=cf-playwright] CF HTML challenge → Playwright directly (same TLS fix),
///           [fallback=cookies]       non-CF 403 → FlareSolverr session cookies, then
///           [fallback=playwright]    headless Playwright browser.
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
    // RequiresBrowserAuth=true drives the admin UI's "Authenticate" button.
    // The OAuth implicit flow requires a browser: user visits MMF's consent screen,
    // approves, and MMF redirects back to our /auth/mmf/callback with the access_token
    // in the URL fragment. Without OAuth, manifest sync still works (username+password
    // Playwright login), but file downloads will 403.
    public bool RequiresBrowserAuth => true;

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
            HelpText = "MMF OAuth client ID. Default 'downloader_v2' works for most users. Pair with CLIENT_SECRET to enable the authorization-code flow for authenticated file downloads.",
        },
        new PluginConfigField
        {
            Key = "CLIENT_SECRET",
            Label = "OAuth Client Secret",
            Type = PluginConfigFieldType.Secret,
            Required = false,
            HelpText = "OAuth client secret. For MMF's built-in 'downloader_v2' client, use the public secret '6b511607-740d-49ad-8e31-3bb8b75dd354' (same for all users — hardcoded in MiniDownloader's source). Leave blank for manifest-only mode (no authenticated file downloads).",
        },
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
            Key = "VERBOSE_LOGGING",
            Label = "Verbose login/sync diagnostics",
            Type = PluginConfigFieldType.String,
            Required = false,
            DefaultValue = "false",
            HelpText = "Set to 'true' to emit detailed step-by-step login + sync logs (Playwright phases, credential shape fingerprint, cookie names, etc.). Off by default — helpful only when diagnosing a new login failure. Errors and final outcomes (Login SUCCESS / Login FAILED) are always logged regardless of this setting.",
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

    public async Task<AuthResult> AuthenticateAsync(PluginContext context, CancellationToken ct = default)
    {
        var username = context.Config.TryGetValue("MMF_USERNAME", out var u) ? u : null;
        var password = context.Config.TryGetValue("MMF_PASSWORD", out var p) ? p : null;

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return AuthResult.Failed("MMF_USERNAME and MMF_PASSWORD must be configured in the plugin settings");

        // OAuth implicit flow: CLIENT_ID + CLIENT_SECRET + CALLBACK_URL trigger a browser
        // consent screen at auth.myminifactory.com/web/authorize. The access_token comes
        // back as a URL fragment; our callback HTML extracts + POSTs it to /auth/mmf/callback.
        // Token validity is verified via a live GET /api/v2/user ping (implicit flow doesn't
        // issue expiry timestamps we can trust locally).
        var clientId = context.Config.TryGetValue("CLIENT_ID", out var cid) ? cid : null;
        var clientSecret = context.Config.TryGetValue("CLIENT_SECRET", out var cs) ? cs : null;
        var callbackUrl = context.Config.TryGetValue("CALLBACK_URL", out var cb) ? cb : null;

        if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret) && !string.IsNullOrEmpty(callbackUrl))
        {
            // Check if we have a stored token that's still valid via a live API ping.
            // Implicit flow doesn't issue expiry timestamps, so we verify by trying the token.
            var existingToken = await context.TokenStore.GetTokenAsync("access_token", ct);
            if (!string.IsNullOrEmpty(existingToken))
            {
                const string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
                using var verifyReq = new HttpRequestMessage(HttpMethod.Get, "https://www.myminifactory.com/api/v2/user");
                verifyReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", existingToken);
                verifyReq.Headers.UserAgent.ParseAdd(userAgent);
                verifyReq.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                try
                {
                    using var resp = await context.HttpClient.SendAsync(verifyReq, ct);
                    if (resp.IsSuccessStatusCode)
                        return AuthResult.Success("OAuth token verified against /api/v2/user — ready to sync");
                    context.Logger.LogDebug("[MMF][auth] Stored token failed live check ({Status}) — requesting re-auth", resp.StatusCode);
                }
                catch (Exception ex)
                {
                    context.Logger.LogDebug("[MMF][auth] Live token check failed (network: {Error}) — requesting re-auth", ex.Message);
                }
            }

            // No valid token — build authorization URL for implicit flow
            var state = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            await context.TokenStore.SaveTokenAsync("oauth_state", state, ct);

            var authUrl = $"https://auth.myminifactory.com/web/authorize"
                + $"?client_id={Uri.EscapeDataString(clientId)}"
                + $"&response_type=token"
                + $"&redirect_uri={Uri.EscapeDataString(callbackUrl)}"
                + $"&state={state}";

            return AuthResult.NeedsBrowser(authUrl, "MMF OAuth authorization required — click to connect");
        }

        // No OAuth config — manifest-only mode (Playwright login works, downloads will 403)
        return AuthResult.Success("Credentials configured — manifest-only mode (add OAuth CLIENT_ID/CLIENT_SECRET for file downloads)");
    }

    public async Task<AuthResult> HandleAuthCallbackAsync(PluginContext context, IDictionary<string, string> callbackParams, CancellationToken ct = default)
    {
        // Implicit flow: access_token arrives as a URL fragment, extracted by our callback
        // page HTML and POSTed to /auth/mmf/callback as a query param.
        if (callbackParams.TryGetValue("access_token", out var accessToken) && !string.IsNullOrEmpty(accessToken))
        {
            await context.TokenStore.SaveTokenAsync("access_token", accessToken, ct);
            context.Logger.LogInformation("[MMF][auth] Access token saved from OAuth implicit-flow callback");
            return AuthResult.Success("Connected to MyMiniFactory via OAuth");
        }

        if (callbackParams.TryGetValue("error", out var error))
        {
            var errorDesc = callbackParams.TryGetValue("error_description", out var desc) ? desc : "";
            context.Logger.LogWarning("[MMF][auth] OAuth callback returned error: {Error} — {Desc}", error, errorDesc);
            return AuthResult.Failed($"Auth error: {error} — {errorDesc}");
        }

        return AuthResult.Failed("No access_token received in callback — ensure the OAuth redirect URI is configured correctly");
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
        IReadOnlyList<ScrapedModel> models = uploadedManifest is not null
            ? await ParseUploadedManifestAsync(uploadedManifest, ct)
            : await FetchLibraryViaBrowserAsync(context, ct);

        // Log a rollup of non-object entries that will be skipped during scrape.
        // Bundles and collections have a different API surface; only individual objects
        // are supported. The skip fires in ScrapeModelAsync per-item; this gives a
        // production-visible summary at manifest load time.
        var skipCount = models.Count(m =>
        {
            var (_, t) = SplitManifestId(m.ExternalId);
            return t != "object" || (m.Type != null && m.Type != "object");
        });
        if (skipCount > 0)
            context.Logger.LogInformation(
                "[MMF] Skipping {Count} non-object entries from manifest (bundles, collections)",
                skipCount);

        return models;
    }

    public async Task<ScrapeResult> ScrapeModelAsync(PluginContext context, ScrapedModel model, CancellationToken ct = default)
    {
        var modelDir = context.ModelDirectory
            ?? throw new InvalidOperationException("ModelDirectory not set in context");

        var delayMs = GetDelayMs(context);
        var downloadDelayMs = GetDownloadDelayMs(context);
        var restoreMode = IsRestoreMode(context);

        // Get OAuth access_token for authenticated API calls and file downloads
        var bearerToken = await context.TokenStore.GetTokenAsync("access_token", ct);

        // FlareSolverr session cookies — used as fallback when Bearer returns 403
        var sessionCookies = await context.TokenStore.GetTokenAsync("session_cookies", ct);
        var sessionUA = await context.TokenStore.GetTokenAsync("session_useragent", ct);

        // Parse manifest ID — items come as 'object-786967', 'bundle-2447', or bare numeric IDs.
        // For bare numeric IDs, SplitManifestId returns "object" by default; also check
        // model.Type (populated from the manifest's 'type' field) to catch bundles/collections
        // that arrive without a prefixed ExternalId.
        var (numericId, idType) = SplitManifestId(model.ExternalId);
        var resolvedType = idType != "object" ? idType : (model.Type ?? "object");
        if (resolvedType != "object")
        {
            context.Logger.LogInformation(
                "[MMF] Skipping {Type} entry {Id} ({Name}) — only individual objects are supported",
                resolvedType, model.ExternalId, model.Name ?? "<no name>");
            return ScrapeResult.Ok("metadata.json", []);
        }

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
                    // HTML response guard (CF TLS fingerprint mismatch — challenge page instead of JSON).
                    // Fall back to Playwright EvaluateAsync which runs inside Chromium (the fingerprint
                    // CF issued the clearance to).
                    if (objJson.TrimStart().StartsWith("<"))
                    {
                        context.Logger.LogWarning(
                            "[MMF] Got HTML response for model {Id} — CF challenge, trying Playwright fallback",
                            model.ExternalId);
                        objJson = numericId is null ? "" : await FetchObjectDetailsViaBrowserAsync(numericId, bearerToken, context.Logger, ct) ?? "";
                        if (string.IsNullOrEmpty(objJson) || objJson.TrimStart().StartsWith("<"))
                        {
                            context.Logger.LogError(
                                "[MMF] Playwright fallback also returned HTML/empty for model {Id} — CF challenge unresolved",
                                model.ExternalId);
                            return ScrapeResult.Failure($"CF HTML challenge for {model.Name} (Playwright fallback also failed)");
                        }
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
                    context.Logger.LogWarning("[MMF] API {Status} for model {Id} ({Name}) — will try archive fallback", response.StatusCode, model.ExternalId, model.Name);
                    // Non-404 failure — still try archive_download_url below
                }
                await Task.Delay(delayMs, ct);

                // ── Step 2: Fetch file download URLs with pagination (if inline was empty) ──
                if (details != null && (details.Files == null || details.Files.Count == 0))
                {
                    var allFiles = new List<MmfFile>();
                    int page = 1;
                    bool hasMore = true;
                    while (hasMore)
                    {
                        var filesResponse = await apiClient.GetAsync($"/api/v2/objects/{numericId}/files?per_page=100&page={page}", ct);
                        if (!filesResponse.IsSuccessStatusCode) break;

                        var filesJson = await filesResponse.Content.ReadAsStringAsync(ct);
                        if (filesJson.TrimStart().StartsWith("<")) break; // HTML guard

                        using var filesDoc = JsonDocument.Parse(filesJson);
                        if (filesDoc.RootElement.TryGetProperty("items", out var fileItems)
                            && fileItems.ValueKind == JsonValueKind.Array)
                        {
                            var parsed = ParseFiles(fileItems);
                            allFiles.AddRange(parsed);
                            // Check if there are more pages
                            var totalCount = filesDoc.RootElement.TryGetProperty("total_count", out var tc) ? tc.GetInt32() : 0;
                            hasMore = totalCount > allFiles.Count && parsed.Count > 0;
                            page++;
                        }
                        else break;

                        if (hasMore) await Task.Delay(delayMs, ct);
                    }
                    if (allFiles.Count > 0)
                        details.Files = allFiles;
                    await Task.Delay(delayMs, ct);
                }

                // ── Step 3: Fallback to archive_download_url (when no individual files found) ──
                if (details != null && (details.Files == null || details.Files.Count == 0) && !string.IsNullOrEmpty(archiveDownloadUrl))
                {
                    var archiveName = $"{SanitizeFilename(model.Name)}.zip";
                    details.Files = [new MmfFile { Filename = archiveName, DownloadUrl = archiveDownloadUrl, Size = 0 }];
                    context.Logger.LogInformation("[MMF] Using archive_download_url for {Name} (no individual files)", model.Name);
                }
            }
            else
            {
                context.Logger.LogWarning("[MMF] No bearer token — skipping API details for {Model}", model.Name);
            }

            // ── Step 3b: Last resort — try archive URL even if details call failed entirely ──
            // The archive_download_url from the objectPreviews manifest might work even when
            // the v2 API details call fails (different auth/CDN path)
            if (details == null || (details.Files == null || details.Files.Count == 0))
            {
                // Try constructing archive URL from known MMF patterns
                var fallbackArchiveUrl = $"https://www.myminifactory.com/download/object-{numericId}";
                context.Logger.LogInformation("[MMF] Trying fallback archive URL for {Name}", model.Name);
                details ??= new MmfModelDetails();
                details.Files = [new MmfFile { Filename = $"{SanitizeFilename(model.Name)}.zip", DownloadUrl = fallbackArchiveUrl, Size = 0 }];
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
                            context.Logger.LogInformation("[MMF][download] Downloading: {File} for {Model} (attempt {Attempt}/3)",
                                safeName, model.Name, attempt + 1);

                            // Primary path: Bearer auth + manual CDN redirect (no auth forwarded).
                            // AllowAutoRedirect=false so we can strip Authorization before following
                            // the 302 to a signed S3 URL (AWS rejects presigned URLs with extra auth).
                            using var dlHandler = new HttpClientHandler { AllowAutoRedirect = false };
                            using var dlClient = new HttpClient(dlHandler) { Timeout = TimeSpan.FromMinutes(30) };

                            using var fileResponse = await DownloadFileWithCleanRedirectAsync(
                                dlClient, file.DownloadUrl, bearerToken, ct);

                            // Check for 404 — don't retry, file doesn't exist
                            if (fileResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                            {
                                context.Logger.LogWarning("[MMF][download] 404 for {File} — skipping (file not found)", safeName);
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
                                    "[MMF][download] 429 Too Many Requests for {File} — waiting {Wait}s (attempt {Attempt}/3)",
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
                                    "[MMF][download] Server error {Status} for {File} (attempt {Attempt}/3)",
                                    (int)fileResponse.StatusCode, safeName, attempt + 1);
                                continue; // next attempt
                            }

                            // 403 fallback chain — wrapped in TryFallbackDownloadAsync:
                            //   [fallback=cf-playwright] CF HTML challenge → skip cookies (same TLS issue), Playwright only
                            //   [fallback=cookies]       non-CF 403 → FlareSolverr session cookies
                            //   [fallback=playwright]    headless Playwright browser (Cloudflare bypass)
                            bool downloadedByBrowser = false;
                            if (fileResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
                            {
                                // Peek at body to detect CF HTML challenge pages.
                                // CF TLS fingerprint mismatch — cookies via HttpClient have the same
                                // fingerprint problem, so skip the cookie fallback and go straight to
                                // Playwright (Chromium — the fingerprint CF actually issued to).
                                var forbiddenBody = await fileResponse.Content.ReadAsStringAsync(ct);
                                var isCfChallenge = IsCfHtmlChallenge(fileResponse, forbiddenBody);
                                if (isCfChallenge)
                                    context.Logger.LogWarning(
                                        "[MMF][download] 403 CF HTML challenge for {File} — skipping cookie fallback, routing through Playwright",
                                        safeName);
                                downloadedByBrowser = await TryFallbackDownloadAsync(
                                    file.DownloadUrl, filePath, bearerToken,
                                    isCfChallenge ? null : sessionCookies,  // skip cookies for CF TLS fingerprint issue
                                    sessionUA, safeName, context.Logger, ct);
                                if (!downloadedByBrowser)
                                    throw new Exception(
                                        $"All download methods ({(isCfChallenge ? "Bearer, Playwright (CF HTML challenge)" : "Bearer, cookies, Playwright")}) returned 403 for {safeName}");
                            }

                            if (!downloadedByBrowser)
                            {
                                fileResponse.EnsureSuccessStatusCode();
                                await using var fs = File.Create(filePath);
                                await fileResponse.Content.CopyToAsync(fs, ct);
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

            // ── Step 4b: If ALL individual file downloads failed, try archive as last resort ──
            if (filesDownloaded == 0 && filesFailed > 0 && !string.IsNullOrEmpty(archiveDownloadUrl))
            {
                context.Logger.LogInformation("[MMF] All {Failed} individual downloads failed for {Name} — trying archive URL", filesFailed, model.Name);
                var archiveName = $"{SanitizeFilename(model.Name)}.zip";
                var archivePath = Path.Combine(modelDir, archiveName);
                try
                {
                    using var archClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                    if (!string.IsNullOrEmpty(bearerToken))
                        archClient.DefaultRequestHeaders.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
                    archClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                    var archResp = await archClient.GetAsync(archiveDownloadUrl, ct);
                    if (archResp.IsSuccessStatusCode)
                    {
                        await using var fs = File.Create(archivePath);
                        await archResp.Content.CopyToAsync(fs, ct);
                        downloadedFiles.Add(new DownloadedFile
                        {
                            Filename = archiveName, LocalPath = archivePath,
                            Size = new FileInfo(archivePath).Length,
                            Variant = "archive", IsArchive = true,
                        });
                        filesDownloaded++;
                        filesFailed = 0; // Archive covers all files
                        context.Logger.LogInformation("[MMF] Archive download succeeded for {Name}", model.Name);
                    }
                }
                catch (Exception ex)
                {
                    context.Logger.LogWarning("[MMF] Archive fallback also failed for {Name}: {Error}", model.Name, ex.Message);
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

    /// <summary>
    /// Returns the outerHTML of the FIRST element matching <paramref name="selector"/> on
    /// <paramref name="page"/>, or empty string if nothing matches. Never throws; the whole
    /// point of this helper is to be safe to call from a diagnostic code path.
    /// </summary>
    private static async Task<string> SafeEvalFirstOuterHtmlAsync(IPage page, string selector)
    {
        try
        {
            var locator = page.Locator(selector).First;
            if (await locator.CountAsync() == 0) return "";
            return await locator.EvaluateAsync<string>("el => el.outerHTML") ?? "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// Returns concatenated outerHTML of ALL elements matching <paramref name="selector"/>,
    /// joined with " | ". Empty string if nothing matches or an error occurs.
    /// </summary>
    private static async Task<string> SafeEvalAllOuterHtmlAsync(IPage page, string selector)
    {
        try
        {
            var allHtml = await page.EvaluateAsync<string[]>(@"sel => Array.from(document.querySelectorAll(sel)).map(el => el.outerHTML)", selector);
            return allHtml == null ? "" : string.Join(" | ", allHtml.Where(h => !string.IsNullOrWhiteSpace(h)));
        }
        catch { return ""; }
    }

    /// <summary>
    /// Builds a signal-dense excerpt of an HTML page for diagnostic logging.
    /// MMF's login page front-loads ~10 KB of <head> markup (meta tags, link preloads,
    /// NewRelic / GTM / matomo scripts) before the actual <body> content starts, which
    /// made excerpt caps of 1500 / 3000 chars useless — every sample was pure boilerplate.
    /// This helper aggressively removes everything we don't care about for debugging:
    ///   1. The entire <head>…</head> block (meta, link, scripts, styles, analytics).
    ///   2. Any remaining <script> or <style> blocks outside of <head>.
    ///   3. Most HTML comments (<!-- … -->).
    ///   4. Runs of whitespace collapsed to a single space.
    /// The result is body markup only: form fields, error messages, flash notices, etc.
    /// </summary>
    private static string BuildDomExcerpt(string html, int maxChars = 3000)
    {
        if (string.IsNullOrEmpty(html)) return "";

        const System.Text.RegularExpressions.RegexOptions Opts =
            System.Text.RegularExpressions.RegexOptions.Singleline |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase;

        var stripped = html;
        // 1. Drop entire <head>…</head>.
        stripped = System.Text.RegularExpressions.Regex.Replace(stripped, @"<head[^>]*>.*?</head>", "", Opts);
        // 2. Drop any <script> or <style> blocks that live outside <head>.
        stripped = System.Text.RegularExpressions.Regex.Replace(stripped, @"<(script|style)[^>]*>.*?</\1>", "", Opts);
        // 3. Drop HTML comments.
        stripped = System.Text.RegularExpressions.Regex.Replace(stripped, @"<!--.*?-->", "", Opts);
        // 4. Collapse whitespace.
        stripped = System.Text.RegularExpressions.Regex.Replace(stripped, @"\s+", " ").Trim();

        return stripped.Length > maxChars ? stripped[..maxChars] + "…" : stripped;
    }

    /// <summary>
    /// Creates a fresh headless Chromium context for the login flow, seeded with CF-cleared
    /// cookies from a prior FlareSolverr GET. Unlike <see cref="GetBrowserContextAsync"/>,
    /// this context has no Authorization header injection — it is solely for form-based login.
    /// The caller owns all three returned instances and MUST dispose them (try/finally).
    /// </summary>
    private static async Task<(IPlaywright Playwright, IBrowser Browser, IBrowserContext Context)> GetLoginBrowserContextAsync(
        IEnumerable<(string Name, string Value)> cfCookies,
        string userAgent,
        ILogger logger,
        bool verbose = false)
    {
        if (verbose)
            logger.LogInformation("[MMF] Launching headless Chromium for Playwright login...");
        var playwright = await Playwright.CreateAsync();
        var chromiumPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH");
        // Container-friendly Chromium args. Without these, system Chromium running as root
        // fails silently with net::ERR_SOCKET_NOT_CONNECTED on the first navigation because:
        //  - Chromium refuses to run in its default sandbox as uid=0
        //  - /dev/shm in k8s pods defaults to 64MB which Chrome's shared-memory regions can exhaust
        // Both symptoms surface as the opaque SOCKET_NOT_CONNECTED error. Fix is the standard
        // Docker/K8s Chromium trio: --no-sandbox, --disable-dev-shm-usage, --disable-gpu.
        var containerArgs = new[]
        {
            "--no-sandbox",
            "--disable-setuid-sandbox",
            "--disable-dev-shm-usage",
            "--disable-gpu",
        };
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = !string.IsNullOrEmpty(chromiumPath) ? chromiumPath : null,
            Args = containerArgs,
        });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = userAgent,
            ExtraHTTPHeaders = new Dictionary<string, string> { ["Accept-Language"] = "en-US,en;q=0.9" },
        });

        // Seed CF-cleared cookies so Playwright skips the Cloudflare challenge that FlareSolverr
        // already solved. Only non-empty name/value pairs are seeded.
        var cookiesToSeed = cfCookies
            .Where(c => !string.IsNullOrEmpty(c.Name) && !string.IsNullOrEmpty(c.Value))
            .Select(c => new Microsoft.Playwright.Cookie
            {
                Name   = c.Name,
                Value  = c.Value,
                Domain = ".myminifactory.com",
                Path   = "/",
            })
            .ToList();

        if (cookiesToSeed.Count > 0)
        {
            await context.AddCookiesAsync(cookiesToSeed);
            if (verbose)
                logger.LogInformation("[MMF] Seeded {Count} CF cookies into Playwright login context",
                    cookiesToSeed.Count);
        }

        return (playwright, browser, context);
    }

    private async Task<IBrowserContext> GetBrowserContextAsync(string bearerToken, ILogger logger)
    {
        if (_browserContext != null) return _browserContext;

        logger.LogInformation("[MMF] Launching headless Chromium for 403 fallback downloads...");
        _playwright = await Playwright.CreateAsync();
        // Use system Chromium if available (Docker image), fall back to Playwright's bundled version
        var chromiumPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH");
        // Container-friendly args (see GetLoginBrowserContextAsync for full explanation)
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = !string.IsNullOrEmpty(chromiumPath) ? chromiumPath : null,
            Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage", "--disable-gpu" },
        });
        _browserContext = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            ExtraHTTPHeaders = new Dictionary<string, string> { ["Accept-Language"] = "en-US,en;q=0.9" }
        });

        // Inject Authorization header ONLY on www.myminifactory.com (the origin API host).
        // CDN hosts like dl4.myminifactory.com serve presigned S3 URLs — those reject
        // presigned requests that carry any additional Authorization header with 403.
        // Forwarding Bearer to the CDN is what caused "Playwright did not capture CDN URL"
        // on every download attempt.
        await _browserContext.RouteAsync("**/www.myminifactory.com/**", async route =>
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
                    logger.LogInformation("[MMF] Captured CDN URL for {File}: {Url}",
                        Path.GetFileName(filePath),
                        cdnUrl.Length > 100 ? cdnUrl[..100] + "..." : cdnUrl);

                    using var dlClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
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
                        var bytes = new FileInfo(filePath).Length;
                        logger.LogInformation("[MMF] CDN fetch returned {Status} ({Bytes} bytes) for {File}",
                            cdnResponse.StatusCode, bytes, Path.GetFileName(filePath));
                        return true;
                    }

                    logger.LogWarning("[MMF] CDN fetch returned {Status} ({Bytes} bytes) for {File} — download failed",
                        cdnResponse.StatusCode,
                        (await cdnResponse.Content.ReadAsByteArrayAsync(ct)).Length,
                        Path.GetFileName(filePath));
                }
                else
                {
                    logger.LogWarning("[MMF] Playwright did not capture CDN URL for {File} (url={Url})",
                        Path.GetFileName(filePath), url);
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
            try { await Task.WhenAny(_unzipTask, Task.Delay(TimeSpan.FromMinutes(30))); } catch { }
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
        var verbose = IsVerbose(context);

        context.Logger.LogInformation("[MMF] Starting library fetch via FlareSolverr + Playwright login...");
        if (verbose)
            context.Logger.LogInformation("[MMF] VERBOSE_LOGGING=true — emitting detailed login/sync trace. Disable via plugin config when not debugging.");

        try
        {
            // Step 1: Get CF cookies via FlareSolverr
            var cfCookies = new List<(string Name, string Value)>();
            string? solvedUserAgent = null;
            // Holds the library JSON fetched INSIDE the Playwright browser context (CF-proof).
            // Set in Step 2 inline (before loginPage.CloseAsync) and consumed below.
            string? browserLibraryJson = null;

            if (!string.IsNullOrEmpty(flareSolverrUrl))
            {
                if (verbose)
                    context.Logger.LogInformation("[MMF] Step 1: Login via FlareSolverr (CF bypass + session)...");
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
                
                // 1a: Create FlareSolverr session
                var createResp = await httpClient.PostAsync($"{flareSolverrUrl}/v1",
                    new StringContent(JsonSerializer.Serialize(new { cmd = "sessions.create" }), System.Text.Encoding.UTF8, "application/json"), ct);
                var createBody = await createResp.Content.ReadAsStringAsync(ct);
                if (verbose)
                    context.Logger.LogInformation("[MMF] FlareSolverr create response: {Body}", createBody.Length > 200 ? createBody[..200] : createBody);
                var createJson = JsonDocument.Parse(createBody);
                if (!createJson.RootElement.TryGetProperty("session", out var sessionProp))
                {
                    context.Logger.LogError("[MMF] FlareSolverr failed to create session: {Body}", createBody[..Math.Min(500, createBody.Length)]);
                    return [];
                }
                var fsSession = sessionProp.GetString();
                if (verbose)
                    context.Logger.LogInformation("[MMF] FlareSolverr session: {Session}", fsSession);

                // 1b: Get login page via FlareSolverr — CF bypass only.
                // We harvest the CF-cleared cookies (cf_clearance, __cflb, __cf_bm) to seed into
                // Playwright's context so it skips the challenge. CSRF is NOT extracted here;
                // Playwright's own GET /login will render a fresh session-scoped CSRF in the DOM.
                var loginPageResp = await httpClient.PostAsync($"{flareSolverrUrl}/v1",
                    new StringContent(JsonSerializer.Serialize(new { cmd = "request.get", url = "https://www.myminifactory.com/login", session = fsSession, maxTimeout = 60000 }), System.Text.Encoding.UTF8, "application/json"), ct);
                var loginPageBody = await loginPageResp.Content.ReadAsStringAsync(ct);
                if (verbose)
                    context.Logger.LogInformation("[MMF] Login page response length: {Len}", loginPageBody.Length);
                var loginPageJson = JsonDocument.Parse(loginPageBody);
                if (!loginPageJson.RootElement.TryGetProperty("solution", out var loginSol))
                {
                    context.Logger.LogError("[MMF] FlareSolverr login page failed: {Body}", loginPageBody[..Math.Min(500, loginPageBody.Length)]);
                    return [];
                }
                // Harvest CF-cleared cookies from FlareSolverr's GET response.
                // These let Playwright skip the Cloudflare challenge it already solved.
                if (loginSol.TryGetProperty("cookies", out var fsCookies))
                {
                    foreach (var cookie in fsCookies.EnumerateArray())
                    {
                        cfCookies.Add((
                            Name:  cookie.GetProperty("name").GetString() ?? "",
                            Value: cookie.GetProperty("value").GetString() ?? ""
                        ));
                    }
                }
                if (loginSol.TryGetProperty("userAgent", out var fsUaProp))
                    solvedUserAgent = fsUaProp.GetString();

                if (verbose)
                    context.Logger.LogInformation(
                        "[MMF] FlareSolverr CF GET complete: {Count} cookies harvested, UA captured={HasUA}",
                        cfCookies.Count, !string.IsNullOrEmpty(solvedUserAgent));

                // Destroy FlareSolverr session — done with it after CF cookie harvest.
                try { await httpClient.PostAsync($"{flareSolverrUrl}/v1",
                    new StringContent(JsonSerializer.Serialize(new { cmd = "sessions.destroy", session = fsSession }), System.Text.Encoding.UTF8, "application/json"), ct); } catch {}

                // 1c: Playwright-based login (replaces FlareSolverr request.post)
                //
                // Why: FlareSolverr's _post_request() (src/flaresolverr_service.py:425) double-URL-encodes
                // form values: it calls unquote(val) then quote(val) in Python, then the headless browser
                // auto-submits via a form which URL-encodes values a second time. This corrupts opaque
                // tokens like CSRF (long base64-ish strings break on double-encode). Playwright submits
                // the form natively as a real browser, with zero intermediate encoding.
                //
                // Flow: seed CF cookies → Playwright GET /login (fresh session-scoped CSRF in DOM)
                //       → fill _username / _password / _remember_me → click submit
                //       → form POSTs to /login_check (via action attribute) → wait for nav → harvest cookies.
                var playwrightUA = solvedUserAgent
                    ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

                IPlaywright?     pwInstance = null;
                IBrowser?        pwBrowser  = null;
                IBrowserContext? pwContext  = null;
                string? loginFailureInfo = null;
                bool    loginSuccess     = false;

                try
                {
                    (pwInstance, pwBrowser, pwContext) =
                        await GetLoginBrowserContextAsync(cfCookies, playwrightUA, context.Logger, verbose);

                    var loginPage = await pwContext.NewPageAsync();

                    // GET /login — Playwright renders a fresh session-scoped CSRF token in the DOM.
                    // MMF's login page is React-rendered as of the "New Library" rollout (April 2026);
                    // server markup returns a bootstrap shell, and the <form> is mounted by React after
                    // JS loads. We wait for NetworkIdle (bounded) so the form is hydrated before we try
                    // to interact with it.
                    if (verbose)
                        context.Logger.LogInformation("[MMF] Playwright: navigating to /login...");
                    await loginPage.GotoAsync(
                        "https://www.myminifactory.com/login",
                        new PageGotoOptions { Timeout = 30000, WaitUntil = WaitUntilState.DOMContentLoaded });
                    try { await loginPage.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }); }
                    catch (TimeoutException) { /* bounded — React may still be idle enough */ }
                    try
                    {
                        await loginPage.WaitForSelectorAsync(
                            "input[name='_username']",
                            new PageWaitForSelectorOptions { Timeout = 15000, State = WaitForSelectorState.Visible });
                    }
                    catch (TimeoutException) { /* fall through — Fill call below will fail with a clearer error */ }

                    // Diagnostic: log credential SHAPE (not values). Used to diagnose Symfony
                    // "Invalid credentials" when browser login works but plugin login does not.
                    // Catches:
                    //   - stored password truncated on save (UI/form-encoding bug)
                    //   - hidden leading/trailing whitespace on stored value
                    //   - non-ASCII chars the keyboard event pipeline may mishandle
                    // We log: character count, ASCII range summary, leading/trailing-whitespace flag,
                    //         and the SHA-256 fingerprint of the stored value (first 8 hex chars).
                    // A fingerprint lets the operator compare 'what's stored' against 'what browser
                    // uses' by running `echo -n 'mypassword' | sha256sum | cut -c1-8` locally —
                    // without ever emitting the plaintext.
                    var pwLen            = password.Length;
                    var pwHasLeadWs      = password.Length > 0 && char.IsWhiteSpace(password[0]);
                    var pwHasTrailWs     = password.Length > 0 && char.IsWhiteSpace(password[^1]);
                    var pwNonAsciiCount  = password.Count(c => c > 127);
                    var pwAsciiPunctCnt  = password.Count(c => c <= 127 && !char.IsLetterOrDigit(c));
                    var pwFingerprint    = string.Empty;
                    try
                    {
                        using var sha = System.Security.Cryptography.SHA256.Create();
                        var digest = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(password));
                        pwFingerprint = Convert.ToHexString(digest)[..8].ToLowerInvariant();
                    }
                    catch { /* never fail diagnostics */ }
                    if (verbose)
                    {
                        context.Logger.LogInformation(
                            "[MMF] credential shape: username.length={UserLen} password.length={PwLen} " +
                            "pw.leadWs={LeadWs} pw.trailWs={TrailWs} pw.nonAscii={NonAscii} " +
                            "pw.asciiPunct={Punct} pw.sha256-8={Fp} (values NEVER logged)",
                            username.Length, pwLen, pwHasLeadWs, pwHasTrailWs, pwNonAsciiCount,
                            pwAsciiPunctCnt, pwFingerprint);
                    }

                    // Type credentials. PressSequentiallyAsync fires keydown/keypress/input events
                    // (as opposed to FillAsync which writes to .value directly). Delay=0 keeps it fast.
                    await loginPage.Locator("input[name='_username']").PressSequentiallyAsync(username, new LocatorPressSequentiallyOptions { Delay = 0 });
                    await loginPage.Locator("input[name='_password']").PressSequentiallyAsync(password, new LocatorPressSequentiallyOptions { Delay = 0 });

                    // Diagnostic: verify what Playwright actually put in the DOM after typing.
                    // If Playwright swallowed characters (keyboard layout issues, escape chars, etc.)
                    // the DOM.value.length will differ from our input length. Check BOTH user + pw.
                    try
                    {
                        var domUserLen = await loginPage.Locator("input[name='_username']").EvaluateAsync<int>("el => el.value.length");
                        var domPwLen   = await loginPage.Locator("input[name='_password']").EvaluateAsync<int>("el => el.value.length");
                        if (domUserLen != username.Length || domPwLen != pwLen)
                        {
                            // MISMATCH is always logged — this is a real problem the operator needs to see.
                            context.Logger.LogWarning(
                                "[MMF] typed-value length MISMATCH — some characters were NOT delivered to the DOM! " +
                                "expected user={ExpectedUser} got user={GotUser}; expected pw={ExpectedPw} got pw={GotPw}",
                                username.Length, domUserLen, pwLen, domPwLen);
                        }
                        else if (verbose)
                        {
                            context.Logger.LogInformation(
                                "[MMF] DOM value lengths match input: user={UserLen} pw={PwLen}",
                                domUserLen, domPwLen);
                        }
                    }
                    catch (Exception diagEx)
                    {
                        context.Logger.LogDebug(diagEx, "[MMF] post-type DOM length check failed (non-fatal)");
                    }

                    // Check remember-me. Guard against it being pre-checked by the page.
                    // (CheckAsync also dispatches a change event, so React picks it up natively.)
                    if (!await loginPage.IsCheckedAsync("input[name='_remember_me']"))
                        await loginPage.CheckAsync("input[name='_remember_me']");

                    // Wait for the submit button to exist and be ready for interaction BEFORE clicking.
                    // Selector uses [name='_submit'] only — works for both <button name="_submit"> and
                    // <input name="_submit"> variants MMF may render. The stricter
                    // button[type='submit'][name='_submit'] selector was fragile: if MMF renders an
                    // <input> or omits type=submit, ClickAsync waits 30s for it to appear and then
                    // throws a TimeoutException that the outer catch mis-labels as a "post-login
                    // navigation" timeout.
                    if (verbose)
                        context.Logger.LogInformation("[MMF] Playwright: submitting login form...");
                    const string SubmitSelector = "[name='_submit']";
                    try
                    {
                        await loginPage.WaitForSelectorAsync(
                            SubmitSelector,
                            new PageWaitForSelectorOptions
                            {
                                Timeout = 15000,
                                State   = WaitForSelectorState.Visible,
                            });
                    }
                    catch (TimeoutException)
                    {
                        // Selector missing = DOM shape changed on MMF's side. Dump current HTML so
                        // we can see what's actually there without a fresh debugging round-trip.
                        var html = await loginPage.ContentAsync();
                        var excerpt = BuildDomExcerpt(html);
                        context.Logger.LogError(
                            "[MMF] Submit button not found on /login (selector={Selector}, url={Url}). DOM excerpt: {Excerpt}",
                            SubmitSelector, loginPage.Url, excerpt);
                        await loginPage.CloseAsync();
                        throw;
                    }

                    // Set up the navigation waiter BEFORE clicking so a fast redirect isn't missed.
                    // The form's action attribute POSTs to /login_check — we don't navigate manually.
                    // Predicate excludes both "/login" and "login_check" (the intermediate POST target).
                    // Timeout 60s — on-disk Chromium + CF edge can take ~20 s just for GET /login.
                    var navTask = loginPage.WaitForURLAsync(
                        url => !url.Contains("/login") && !url.Contains("login_check"),
                        new PageWaitForURLOptions { Timeout = 60000 });
                    await loginPage.ClickAsync(SubmitSelector);
                    try
                    {
                        await navTask;
                    }
                    catch (TimeoutException)
                    {
                        // Fall through — if REMEMBERME was set before the full redirect completed,
                        // login still succeeded. The cookie check below is the source of truth.
                        context.Logger.LogWarning("[MMF] WaitForURLAsync timed out — falling through to cookie check");
                    }

                    // Wait briefly for network idle to ensure cookies are flushed; don't hard-fail.
                    try { await loginPage.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }); } catch (TimeoutException) { }

                    // Extract cookies from Playwright's context (REMEMBERME + CF + session).
                    var finalUrl  = loginPage.Url;
                    var pwCookies = await pwContext.CookiesAsync();
                    var hasRememberMe = pwCookies.Any(c => c.Name == "REMEMBERME");

                    // Replace cfCookies with Playwright's full post-login cookie set.
                    cfCookies.Clear();
                    foreach (var c in pwCookies)
                        cfCookies.Add((c.Name, c.Value));

                    if (hasRememberMe)
                    {
                        context.Logger.LogInformation(
                            "[MMF] Login SUCCESS via Playwright: finalUrl={Url}, cookies={Count}, REMEMBERME=True",
                            finalUrl, cfCookies.Count);
                        loginSuccess = true;

                        // ── Step 2 (inline): Fetch library manifest INSIDE Playwright ──
                        //
                        // CF's cf_clearance cookie is bound to the originating Chromium TLS
                        // fingerprint. HttpClient has a different fingerprint, so CF rejects the
                        // cookie and returns a 403 HTML challenge page even when the cookie is
                        // valid. Running the fetch inside page.EvaluateAsync keeps us in the
                        // Chromium context that earned the clearance — CF honours it.
                        //
                        // Reference: inxaos-repo/mmf-downloader ManifestService.cs:177.
                        if (verbose)
                            context.Logger.LogInformation("[MMF] Step 2: fetching manifest via page.EvaluateAsync inside Playwright...");

                        context.Progress.Report(new ScrapeProgress
                        {
                            Status = "fetching_manifest",
                            CurrentItem = "Fetching library manifest via Playwright...",
                        });

                        try
                        {
                            // Navigate to /my/collections to establish an authenticated page
                            // context before running the fetch. This is a lightweight page that
                            // confirms we're logged in and seeds the session cookies correctly.
                            try
                            {
                                await loginPage.GotoAsync(
                                    "https://www.myminifactory.com/my/collections",
                                    new PageGotoOptions { Timeout = 30000, WaitUntil = WaitUntilState.DOMContentLoaded });
                                try { await loginPage.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 10000 }); } catch { }
                            }
                            catch (Exception navEx)
                            {
                                context.Logger.LogWarning(navEx, "[MMF] Step 2: nav to /my/collections failed — proceeding with direct fetch");
                            }

                            browserLibraryJson = await loginPage.EvaluateAsync<string>(@"
                                async () => {
                                    const resp = await fetch('/api/data-library/objectPreviews', {
                                        credentials: 'include',
                                        headers: { 'Accept': 'application/json' }
                                    });
                                    if (!resp.ok) return JSON.stringify({ error: resp.status, statusText: resp.statusText });
                                    const data = await resp.json();
                                    return JSON.stringify(data);
                                }
                            ");
                            context.Logger.LogInformation(
                                "[MMF] Step 2 via Playwright EvaluateAsync: {Len} chars",
                                browserLibraryJson?.Length ?? 0);
                        }
                        catch (Exception fetchEx)
                        {
                            context.Logger.LogError(fetchEx, "[MMF] Step 2 EvaluateAsync fetch failed — will surface error below");
                        }
                    }
                    else
                    {
                        // Capture diagnostic info while page is still open (cookie values NOT logged).
                        var cookieNames  = string.Join(", ", pwCookies.Select(c => c.Name));

                        // Targeted extract: pull ONLY the <form> + any error/flash markup rather
                        // than dumping the whole body. MMF's top-nav/promo-bar/cookie-consent eats
                        // the excerpt budget before we ever reach the form.
                        //
                        // Each snippet is kept short; we join them so the log line has the
                        // signal-dense parts of the DOM without needing a giant cap.
                        string formHtml   = await SafeEvalFirstOuterHtmlAsync(loginPage, "form[action*='login_check'], form[action*='/login']");
                        string errorsHtml = await SafeEvalAllOuterHtmlAsync(loginPage,
                            ".alert, .flash, .flash-notice, .flash-error, .flash-success, " +
                            "[class*='error'], [class*='Error'], [role='alert'], .invalid-feedback, .help-block.error");
                        string titleText  = await loginPage.TitleAsync();
                        var fallback     = string.IsNullOrWhiteSpace(formHtml) && string.IsNullOrWhiteSpace(errorsHtml)
                            ? BuildDomExcerpt(await loginPage.ContentAsync())
                            : "";

                        loginFailureInfo =
                            $"finalUrl={finalUrl}, title=\"{titleText}\", cookieNames=[{cookieNames}], " +
                            $"formHtml={BuildDomExcerpt(formHtml, 2000)}, " +
                            $"errorsHtml={BuildDomExcerpt(errorsHtml, 2000)}, " +
                            $"fallbackBodyExcerpt={fallback}";
                    }

                    await loginPage.CloseAsync();
                }
                catch (TimeoutException tex)
                {
                    // Generic outer-catch timeout. Inner code paths log their own specific diagnostic
                    // (selector-missing dumps the DOM, WaitForURLAsync falls through to the cookie
                    // check). If we end up HERE, it's something else — probably GotoAsync or a hang
                    // Playwright hasn't surfaced elsewhere. Log the message verbatim so we can tell.
                    context.Logger.LogError(
                        "[MMF] Playwright login threw TimeoutException in unclassified location: {Error}",
                        tex.Message);
                }
                catch (Exception pwEx)
                {
                    context.Logger.LogError(pwEx, "[MMF] Playwright login threw unexpected error");
                }
                finally
                {
                    if (pwContext  != null) try { await pwContext.DisposeAsync(); }  catch { }
                    if (pwBrowser  != null) try { await pwBrowser.DisposeAsync(); }  catch { }
                    if (pwInstance != null) try { pwInstance.Dispose(); }             catch { }
                }

                if (!loginSuccess)
                {
                    if (loginFailureInfo != null)
                        context.Logger.LogError("[MMF] Playwright login FAILED — {Info}", loginFailureInfo);
                    return [];
                }
            }

            // Step 2 result: manifest was fetched INSIDE the Playwright browser context above
            // (CF-proof — uses Chromium's TLS fingerprint). Save cookies for download fallbacks.
            if (verbose)
                context.Logger.LogInformation("[MMF] Step 2: using manifest from Playwright EvaluateAsync");

            // Build + save cookie header for ScrapeModelAsync download fallbacks.
            // These are still useful when CF is NOT challenging (fast-path HttpClient downloads).
            var cookieHeader = string.Join("; ", cfCookies.Select(c => $"{c.Name}={c.Value}"));
            await context.TokenStore.SaveTokenAsync("session_cookies", cookieHeader, ct);
            await context.TokenStore.SaveTokenAsync("session_useragent", solvedUserAgent ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36", ct);
            if (verbose)
                context.Logger.LogInformation("[MMF] Saved session cookies for file downloads");

            var jsonResult = browserLibraryJson;
            if (string.IsNullOrEmpty(jsonResult) || jsonResult.Contains("\"error\""))
            {
                context.Logger.LogError("[MMF] Step 2 manifest failed: {Result}",
                    jsonResult?.Substring(0, Math.Min(200, jsonResult?.Length ?? 0)) ?? "(null — EvaluateAsync did not run or threw)");
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

    /// <summary>
    /// Whether verbose login/sync diagnostics are enabled for this plugin context.
    /// Gate all step-by-step trace logs (Playwright phases, cookie names, credential
    /// shape fingerprints, DOM length matches, etc.) behind this. Business-critical
    /// events — Login SUCCESS, Login FAILED, unexpected errors — are ALWAYS logged
    /// regardless of this setting.
    /// </summary>
    private static bool IsVerbose(PluginContext context)
    {
        if (context.Config.TryGetValue("VERBOSE_LOGGING", out var val))
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

    /// <summary>
    /// Splits an MMF manifest ID like "object-786967" / "bundle-2447" / "collection-999"
    /// or a bare numeric ID into (numericId, type). Returns (null, null) for null/empty.
    /// Bare numeric IDs are treated as "object" type.
    /// </summary>
    internal static (string? NumericId, string? Type) SplitManifestId(string? externalId)
    {
        if (string.IsNullOrEmpty(externalId)) return (null, null);
        var dash = externalId.IndexOf('-');
        if (dash <= 0)
        {
            // No prefix — assume it's already a numeric object id
            return (externalId, "object");
        }
        var prefix = externalId[..dash].ToLowerInvariant();
        var tail = externalId[(dash + 1)..];
        return (tail, prefix);
    }

    /// <summary>
    /// Downloads a file from <paramref name="url"/> with manual CDN redirect handling.
    /// The <paramref name="httpClient"/> MUST have AllowAutoRedirect=false.
    ///
    /// MMF's /download endpoint returns a 302 to a signed S3/CDN URL. We follow it
    /// WITHOUT forwarding the Authorization header — AWS presigned URLs are self-authenticating
    /// and reject any additional Authorization header with 403.
    ///
    /// Returns the final HttpResponseMessage; caller owns and must dispose it.
    /// </summary>
    internal static async Task<HttpResponseMessage> DownloadFileWithCleanRedirectAsync(
        HttpClient httpClient,
        string url,
        string? bearerToken,
        CancellationToken ct = default)
    {
        const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        var mmfReq = new HttpRequestMessage(HttpMethod.Get, url);
        mmfReq.Headers.UserAgent.ParseAdd(UserAgent);
        if (!string.IsNullOrEmpty(bearerToken) && !IsCdnUrl(url))
            mmfReq.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);

        var firstResp = await httpClient.SendAsync(mmfReq, HttpCompletionOption.ResponseHeadersRead, ct);

        if ((int)firstResp.StatusCode == 302 && firstResp.Headers.Location is not null)
        {
            var cdnLocation = firstResp.Headers.Location.IsAbsoluteUri
                ? firstResp.Headers.Location
                : new Uri(new Uri(url), firstResp.Headers.Location);
            firstResp.Dispose();

            // Deliberately no Authorization — signed CDN URLs are self-authenticating
            var cdnReq = new HttpRequestMessage(HttpMethod.Get, cdnLocation);
            cdnReq.Headers.UserAgent.ParseAdd(UserAgent);
            return await httpClient.SendAsync(cdnReq, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        return firstResp;
    }

    /// <summary>
    /// 403 fallback chain for file downloads:
    ///   [fallback=cookies]   — retry with FlareSolverr session cookies (Cloudflare-cleared)
    ///   [fallback=playwright] — retry with headless Playwright browser
    ///
    /// Returns true if the file was downloaded successfully via a fallback path
    /// (the file is written to <paramref name="filePath"/> in that case).
    /// Returns false if all fallbacks failed.
    /// </summary>
    private async Task<bool> TryFallbackDownloadAsync(
        string url, string filePath, string? bearerToken,
        string? sessionCookies, string? sessionUA, string safeName,
        ILogger logger, CancellationToken ct)
    {
        // [fallback=cookies] FlareSolverr session cookies bypass Cloudflare DDoS protection
        if (!string.IsNullOrEmpty(sessionCookies))
        {
            logger.LogWarning("[MMF][fallback=cookies] 403 on Bearer — retrying with session cookies for {File}", safeName);
            try
            {
                using var cookieClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                cookieClient.DefaultRequestHeaders.Add("Cookie", sessionCookies);
                cookieClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                    sessionUA ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                using var cookieResp = await cookieClient.GetAsync(url, ct);
                if (cookieResp.IsSuccessStatusCode)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                    await using var fs = File.Create(filePath);
                    await cookieResp.Content.CopyToAsync(fs, ct);
                    logger.LogInformation("[MMF][fallback=cookies] Download succeeded for {File}", safeName);
                    return true;
                }
                logger.LogWarning("[MMF][fallback=cookies] Cookie download returned {Status} for {File}", cookieResp.StatusCode, safeName);
            }
            catch (Exception ex)
            {
                logger.LogWarning("[MMF][fallback=cookies] Cookie download threw for {File}: {Error}", safeName, ex.Message);
            }
        }

        // [fallback=playwright] Playwright headless browser — captures CDN redirect via response events
        if (!string.IsNullOrEmpty(bearerToken))
        {
            logger.LogWarning("[MMF][fallback=playwright] Retrying with Playwright browser for {File}", safeName);
            return await DownloadWithBrowserAsync(url, filePath, bearerToken, logger, ct);
        }

        return false;
    }

    /// <summary>Check if a URL is a CDN URL (skip Bearer auth for CDN downloads).</summary>
    internal static bool IsCdnUrl(string url)
    {
        return url.Contains("cdn.myminifactory.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("dl.myminifactory.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("dl4.myminifactory.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="response"/> looks like a Cloudflare HTML
    /// challenge page (status 403/503, body starts with <c>&lt;</c>).
    ///
    /// CF challenge pages arise from TLS fingerprint mismatch: the browser that earned the
    /// <c>cf_clearance</c> cookie used Chromium's TLS stack; .NET HttpClient presents a
    /// different fingerprint, so CF rejects the cookie and returns an HTML challenge even when
    /// the cookie value is technically correct.
    /// </summary>
    internal static bool IsCfHtmlChallenge(HttpResponseMessage response, string body)
    {
        // Success responses are never CF challenges
        if (response.IsSuccessStatusCode) return false;

        // Body must start with '<' (HTML document)
        var trimmed = body.TrimStart();
        if (!trimmed.StartsWith("<")) return false;

        // CF challenges are usually 403 or 503
        var status = (int)response.StatusCode;
        if (status != 403 && status != 503) return false;

        // Content-type may be text/html or application/octet-stream; body start is decisive
        return true;
    }

    /// <summary>
    /// Fetch <c>/api/v2/objects/{numericId}</c> inside a Playwright browser context as a
    /// CF-proof fallback when the HttpClient call returns an HTML challenge page.
    ///
    /// The <see cref="GetBrowserContextAsync"/> route handler injects Bearer auth
    /// for <c>www.myminifactory.com</c> requests, so the fetch picks it up automatically.
    /// </summary>
    private async Task<string?> FetchObjectDetailsViaBrowserAsync(
        string numericId, string bearerToken, ILogger logger, CancellationToken ct)
    {
        try
        {
            var browserCtx = await GetBrowserContextAsync(bearerToken, logger);
            var page = await browserCtx.NewPageAsync();
            try
            {
                // Route handler already injects Bearer for www.myminifactory.com —
                // no need to add Authorization manually in the JS fetch.
                var apiPath = $"/api/v2/objects/{numericId}";
                var json = await page.EvaluateAsync<string>(@"async (path) => {
                    const resp = await fetch(path, {
                        credentials: 'include',
                        headers: { 'Accept': 'application/json' }
                    });
                    if (!resp.ok) return JSON.stringify({ error: resp.status, statusText: resp.statusText });
                    return await resp.text();
                }", apiPath);
                logger.LogInformation(
                    "[MMF] FetchObjectDetailsViaBrowserAsync for {Id}: {Len} chars",
                    numericId, json?.Length ?? 0);
                return json;
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[MMF] FetchObjectDetailsViaBrowserAsync threw for {Id}", numericId);
            return null;
        }
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

    /// <summary>
    /// Proactively refreshes the OAuth access_token when it is within 5 minutes of expiry.
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
