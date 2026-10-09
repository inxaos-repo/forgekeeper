using System.Text.Json;
using Forgekeeper.PluginSdk;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Scraper.Mmf;

/// <summary>
/// Phase 2 — session-first auth, manifest fetch and downloads.
/// Order of preference: saved website session (cookies) → OAuth bearer (fallback, ~6h) → legacy paths.
/// </summary>
public partial class MmfScraperPlugin
{
    internal const string SiteBase = "https://www.myminifactory.com";
    internal const string LibraryPath = "/api/data-library/objectPreviews";

    /// <summary>Test seam: replaces Task.Delay for backoff/Retry-After waits in the session path.</summary>
    internal static Func<TimeSpan, CancellationToken, Task>? DelayOverride { get; set; }

    private static Func<TimeSpan, CancellationToken, Task> Delay => DelayOverride ?? Task.Delay;
    private static HttpMessageHandler SessionHandler => ApiHandlerOverride ?? SharedSessionHandler.Value;
    private static readonly Lazy<HttpMessageHandler> SharedSessionHandler = new(() =>
        new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = System.Net.DecompressionMethods.All });

    /// <summary>Import a session from a pasted Cookie header / cookie JSON / Playwright storageState.</summary>
    internal static async Task<AuthResult> ImportSessionAsync(PluginContext context, string raw, string? userAgent, CancellationToken ct)
    {
        string header;
        try { header = MmfSession.NormalizeCookieInput(raw); }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return AuthResult.Failed($"Could not parse cookies: {ex.Message}");
        }
        await context.TokenStore.SaveTokenAsync(MmfSession.CookieKey, header, ct);
        await context.TokenStore.SaveTokenAsync(MmfSession.UserAgentKey,
            string.IsNullOrWhiteSpace(userAgent) ? MmfSession.DefaultUserAgent : userAgent.Trim(), ct);
        await context.TokenStore.SaveTokenAsync(MmfSession.SavedAtKey, DateTime.UtcNow.ToString("O"), ct);
        await context.TokenStore.SaveTokenAsync(MmfSession.StateKey, "ok", ct);
        var names = MmfSession.CookieNames(header);
        context.Logger.LogInformation("[MMF][session] Session imported ({Count} cookies: {Names})", names.Count, string.Join(",", names));
        return AuthResult.Success($"MMF session saved ({names.Count} cookies)");
    }

    internal static async Task<MmfCredentials> GetCredentialsAsync(PluginContext context, CancellationToken ct)
    {
        var cookies = await context.TokenStore.GetTokenAsync(MmfSession.CookieKey, ct);
        var state = await context.TokenStore.GetTokenAsync(MmfSession.StateKey, ct);
        var ua = await context.TokenStore.GetTokenAsync(MmfSession.UserAgentKey, ct);
        var bearer = await context.TokenStore.GetTokenAsync("access_token", ct);
        if (string.Equals(state, "expired", StringComparison.OrdinalIgnoreCase)) cookies = null;
        return new MmfCredentials(cookies, bearer, string.IsNullOrWhiteSpace(ua) ? MmfSession.DefaultUserAgent : ua);
    }

    private static async Task MarkSessionExpiredAsync(PluginContext context, CancellationToken ct)
    {
        await context.TokenStore.SaveTokenAsync(MmfSession.StateKey, "expired", CancellationToken.None);
        context.Logger.LogWarning("[MMF][session] Session rejected by MMF (JSON 401/403) — reconnect needed");
    }

    /// <summary>GET a JSON endpoint on MMF with retries for 429/5xx. Returns (kind, body).</summary>
    internal static async Task<(MmfResponseKind Kind, string? Body)> GetJsonAsync(
        string url, MmfCredentials creds, CancellationToken ct, int maxAttempts = 4)
    {
        using var client = new HttpClient(SessionHandler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) };
        MmfResponseKind kind = MmfResponseKind.Other;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            creds.Apply(req);
            using var resp = await client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            kind = MmfSession.Classify(resp.StatusCode, resp.Content.Headers.ContentType?.MediaType,
                body.Length > 2048 ? body[..2048] : body);
            if (kind == MmfResponseKind.Ok) return (kind, body);
            if (kind is not (MmfResponseKind.RateLimited or MmfResponseKind.ServerError) || attempt == maxAttempts)
                return (kind, null);
            var ra = resp.Headers.RetryAfter?.Delta;
            var wait = ra ?? TimeSpan.FromSeconds(2 * Math.Pow(4, attempt - 1));
            if (wait > TimeSpan.FromMinutes(10)) wait = TimeSpan.FromMinutes(10);
            await Delay(wait, ct);
        }
        return (kind, null);
    }

    /// <summary>Real-sync manifest: the unpaginated library endpoint, authenticated by the session.</summary>
    internal static async Task<IReadOnlyList<ScrapedModel>> FetchLibraryViaSessionAsync(
        PluginContext context, MmfCredentials creds, CancellationToken ct)
    {
        context.Logger.LogInformation("[MMF][session] Fetching library via saved session");
        var (kind, body) = await GetJsonAsync(SiteBase + LibraryPath, creds, ct);
        switch (kind)
        {
            case MmfResponseKind.Ok:
                var (models, stats) = MmfManifestParser.Parse(body!);
                context.Logger.LogInformation("[MMF][session] Library: {Count} models ({Stats})", models.Count, stats);
                return models;
            case MmfResponseKind.AuthExpired:
                await MarkSessionExpiredAsync(context, ct);
                throw new PluginAuthExpiredException("MMF session expired — reconnect needed (re-import cookies)");
            case MmfResponseKind.CloudflareChallenge:
                throw new PluginAuthExpiredException("Cloudflare challenge on library fetch — refresh the session (re-import cookies from a browser that passed the challenge)");
            default:
                throw new InvalidOperationException($"Library fetch failed: {kind}");
        }
    }

    internal static bool IsForceRedownload(PluginContext context) =>
        context.Config.TryGetValue("FORCE_REDOWNLOAD", out var v) && v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Session-based scrape of one object: /api/v2/objects/{id} → archive URL → verified atomic download.</summary>
    internal async Task<ScrapeResult> ScrapeViaSessionAsync(
        PluginContext context, ScrapedModel model, string numericId, MmfCredentials creds, CancellationToken ct)
    {
        var modelDir = context.ModelDirectory!;
        var objectUrl = $"{MmfApiBase}/objects/{numericId}";
        var (kind, body) = await GetJsonAsync(objectUrl, creds, ct);

        if (kind == MmfResponseKind.AuthExpired && creds.HasSession)
        {
            await MarkSessionExpiredAsync(context, ct);
            if (creds.HasBearer)
            {
                context.Logger.LogInformation("[MMF][session] Falling back to OAuth bearer for {Id}", numericId);
                creds = creds with { CookieHeader = null };
                (kind, body) = await GetJsonAsync(objectUrl, creds, ct);
            }
        }

        switch (kind)
        {
            case MmfResponseKind.Ok: break;
            case MmfResponseKind.AuthExpired:
                return ScrapeResult.TokenExpired("MMF session/token expired — reconnect needed");
            case MmfResponseKind.CloudflareChallenge:
                return ScrapeResult.TokenExpired("Cloudflare challenge — refresh the MMF session");
            case MmfResponseKind.NotFound:
                return ScrapeResult.Failure($"Object {numericId} not found (404)");
            default:
                return ScrapeResult.Failure($"Object {numericId} details failed: {kind}");
        }

        MmfModelDetails details;
        string? archiveUrl;
        using (var doc = JsonDocument.Parse(body!))
        {
            var root = doc.RootElement;
            details = ParseModelDetails(root);
            archiveUrl = Str(root, "archive_download_url") ?? Str(root, "download_url");
        }
        if (string.IsNullOrEmpty(archiveUrl))
            return ScrapeResult.Failure($"No download_url/archive_download_url for {numericId}");
        if (!Uri.TryCreate(archiveUrl, UriKind.Absolute, out _)) archiveUrl = SiteBase + "/" + archiveUrl.TrimStart('/');

        Directory.CreateDirectory(modelDir);
        var archiveName = $"{SanitizeFilename(model.Name)}.zip";
        var archivePath = Path.Combine(modelDir, archiveName);
        var extractDir = Path.Combine(modelDir, Path.GetFileNameWithoutExtension(archiveName));
        var files = new List<DownloadedFile>();
        var alreadyHave = File.Exists(archivePath)
            || (Directory.Exists(extractDir) && Directory.EnumerateFileSystemEntries(extractDir).Any());

        if (alreadyHave && !IsForceRedownload(context))
        {
            context.Logger.LogInformation("[MMF][session] {Name}: archive already on disk — skipping download", model.Name);
        }
        else
        {
            var downloader = new MmfDownloader(SessionHandler, new MmfDownloadOptions { Delay = Delay });
            var outcome = await downloader.DownloadAsync(archiveUrl, archivePath, creds, 0, ct);
            switch (outcome.Status)
            {
                case DownloadStatus.Success:
                    files.Add(new DownloadedFile
                    {
                        Filename = archiveName, LocalPath = archivePath, Size = outcome.Bytes,
                        Variant = "archive", IsArchive = true,
                    });
                    break;
                case DownloadStatus.AuthExpired:
                    if (creds.HasSession) await MarkSessionExpiredAsync(context, ct);
                    return ScrapeResult.TokenExpired("Download rejected (auth) — reconnect needed");
                case DownloadStatus.CloudflareChallenge:
                    return ScrapeResult.TokenExpired("Cloudflare challenge on download — refresh the MMF session");
                default:
                    // #28: a failed download is a failure, never a silent success.
                    return ScrapeResult.Failure($"Download failed for {model.Name}: {outcome.Error} after {outcome.Attempts} attempt(s)");
            }
        }

        var metadataPath = Path.Combine(modelDir, "metadata.json");
        Dictionary<string, object?>? existing = null;
        if (File.Exists(metadataPath))
        {
            try { existing = JsonSerializer.Deserialize<Dictionary<string, object?>>(await File.ReadAllTextAsync(metadataPath, ct), JsonOptions); }
            catch { /* start fresh */ }
        }
        var metadata = BuildMetadata(model, details, files, existing);
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, JsonWriteOptions), ct);

        foreach (var dl in files)
            _unzipQueue.Enqueue((dl.LocalPath, extractDir, dl.Filename ?? archiveName));

        var gap = GetDownloadDelayMs(context);
        if (files.Count > 0 && gap > 0) await Task.Delay(gap, ct);
        return ScrapeResult.Ok("metadata.json", files);
    }

    private static string? Str(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString() : null;
}
