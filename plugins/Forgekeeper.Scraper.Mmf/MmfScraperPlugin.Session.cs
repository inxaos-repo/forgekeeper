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

    /// <summary>
    /// Session-path handler for these credentials: through the curl_cffi fetch sidecar when
    /// FETCH_PROXY_URL is configured, otherwise the plain SessionHandler (unchanged behaviour).
    /// </summary>
    internal static HttpMessageHandler SessionHandlerFor(MmfCredentials creds)
    {
        var cfg = creds.FetchProxyUrl is { Length: > 0 } u ? new Dictionary<string, string> { [MmfFetchProxy.ConfigKey] = u } : null;
        if (cfg == null) return SessionHandler;
        return ApiHandlerOverride != null
            ? MmfFetchProxy.Wrap(cfg, ApiHandlerOverride)
            : MmfFetchProxy.Shared(cfg)!;
    }
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
        return new MmfCredentials(cookies, bearer, string.IsNullOrWhiteSpace(ua) ? MmfSession.DefaultUserAgent : ua)
        {
            FetchProxyUrl = MmfFetchProxy.GetUrl(context.Config),
        };
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
        using var client = new HttpClient(SessionHandlerFor(creds), disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) };
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
        var result = await ScrapeViaSessionOnceAsync(context, model, numericId, creds, ct);
        if (result.AuthExpired && result.Error?.StartsWith(CloudflarePrefix, StringComparison.Ordinal) == true
            && MmfFlareSolverr.GetUrl(context) != null)
        {
            context.Logger.LogInformation("[MMF][flaresolverr] Cloudflare challenge on {Id} ({Name}) — asking FlareSolverr for clearance", numericId, model.Name);
            var refreshed = await MmfFlareSolverr.RefreshAsync(context, creds, ct);
            if (refreshed != null)
            {
                result = await ScrapeViaSessionOnceAsync(context, model, numericId, refreshed, ct);
                if (result.AuthExpired)
                    context.Logger.LogWarning("[MMF][flaresolverr] Retry of {Id} still blocked: {Error} — pausing", numericId, result.Error);
            }
        }
        // #48 belt-and-braces: a failure that is really a challenge must pause, never count as failed.
        if (!result.Success && !result.AuthExpired && !result.Skipped && IsChallengeError(result.Error))
        {
            context.Logger.LogWarning("[MMF] {Id}: challenge reported as failure ({Error}) — pausing instead", numericId, result.Error);
            result = ScrapeResult.TokenExpired($"{CloudflarePrefix} — refresh the MMF session ({result.Error})");
        }
        if (result.Success && result.Files.Count > 0)
        {
            var gap = MmfPacing.ItemDelay(context.Config);
            if (gap > TimeSpan.Zero)
            {
                context.Logger.LogDebug("[MMF] Pacing: waiting {Seconds:F1}s before next item", gap.TotalSeconds);
                await Delay(gap, ct);
            }
        }
        return result;
    }

    internal const string CloudflarePrefix = "Cloudflare challenge";

    internal static bool IsChallengeError(string? error) =>
        error != null && (error.Contains("Cloudflare challenge", StringComparison.OrdinalIgnoreCase) || MmfSession.LooksLikeChallengeBody(error));

    private async Task<ScrapeResult> ScrapeViaSessionOnceAsync(
        PluginContext context, ScrapedModel model, string numericId, MmfCredentials creds, CancellationToken ct)
    {
        var modelDir = context.ModelDirectory!;
        var objectUrl = $"{MmfApiBase}/objects/{numericId}";
        var apiGap = MmfPacing.ApiDelay(context.Config);
        if (apiGap > TimeSpan.Zero) await Delay(apiGap, ct);
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
        List<MmfDownloadTarget> targets;
        using (var doc = JsonDocument.Parse(body!))
        {
            var root = doc.RootElement;
            details = ParseModelDetails(root);
            targets = ResolveDownloadTargets(root, model.Name);
            if (targets.Count == 0)
            {
                // #38: some objects (e.g. rules/PDF-only or not-yet-released items) expose no
                // download link at all. Log the shape (key names only) and skip, don't fail.
                var shape = DescribeFilesShape(root);
                if (IsPdfOnlyObject(root))
                {
                    // e.g. 824787 "Of Oil and Iron – Core Rules": category "PDF Only", files.items=0,
                    // /download/{id} redirects back to the object page. Nothing to fetch yet.
                    context.Logger.LogWarning("[MMF][session] {Id} ({Name}) is a PDF-only object but MMF exposes no files yet; shape: {Shape}", numericId, model.Name, shape);
                    return ScrapeResult.Skip($"PDF-only object {numericId}: MMF exposes no downloadable files yet ({shape})");
                }
                context.Logger.LogWarning("[MMF][session] {Id} ({Name}) has no download links; shape: {Shape}", numericId, model.Name, shape);
                return ScrapeResult.Skip($"No downloadable files exposed by MMF for {numericId} ({shape})");
            }
        }

        Directory.CreateDirectory(modelDir);
        var files = new List<DownloadedFile>();
        var force = IsForceRedownload(context);
        var downloader = new MmfDownloader(SessionHandlerFor(creds), new MmfDownloadOptions { Delay = Delay });
        var extractDirs = new Dictionary<string, string>();
        var queued = new List<(string Path, string Extract, string Name)>();
        var downloadedAny = false;
        foreach (var t in targets)
        {
            var path = Path.Combine(modelDir, t.FileName);
            var extractDir = Path.Combine(modelDir, Path.GetFileNameWithoutExtension(t.FileName));
            var alreadyHave = File.Exists(path)
                || (t.IsArchive && Directory.Exists(extractDir) && Directory.EnumerateFileSystemEntries(extractDir).Any());
            if (alreadyHave && !force)
            {
                context.Logger.LogInformation("[MMF][session] {Name}: {File} already on disk — skipping download", model.Name, t.FileName);
                continue;
            }
            if (downloadedAny && apiGap > TimeSpan.Zero) await Delay(MmfPacing.ApiDelay(context.Config), ct);
            downloadedAny = true;
            var outcome = await downloader.DownloadAsync(t.Url, path, creds, 0, ct) /* listed size is informational; not proven byte-exact */;
            switch (outcome.Status)
            {
                case DownloadStatus.Success:
                    files.Add(new DownloadedFile
                    {
                        Filename = t.FileName, LocalPath = path, Size = outcome.Bytes,
                        Variant = t.IsArchive ? "archive" : (ClassifyFileKind(t.FileName) == "document" ? "document" : null),
                        IsArchive = t.IsArchive,
                    });
                    if (t.IsArchive) queued.Add((path, extractDir, t.FileName));
                    break;
                case DownloadStatus.AuthExpired:
                    if (creds.HasSession) await MarkSessionExpiredAsync(context, ct);
                    return ScrapeResult.TokenExpired("Download rejected (auth) — reconnect needed");
                case DownloadStatus.CloudflareChallenge:
                    // #48: pause (reconnectNeeded) — never a failed item; the host keeps the index so resume retries it.
                    context.Logger.LogWarning("[MMF][session] {Name}: {File} — {Error}", model.Name, t.FileName, outcome.Error);
                    return ScrapeResult.TokenExpired($"Cloudflare challenge on download — refresh the MMF session ({outcome.Error})");
                case DownloadStatus.NotAFile:
                    // #53: MMF served a page (e.g. object page after /download redirect) — fail this item, keep going.
                    context.Logger.LogWarning("[MMF][session] {Name}: {File} — {Error}", model.Name, t.FileName, outcome.Error);
                    return ScrapeResult.Failure($"Download failed for {model.Name} ({t.FileName}): {outcome.Error}");
                default:
                    // #28: a failed download is a failure, never a silent success.
                    return ScrapeResult.Failure($"Download failed for {model.Name} ({t.FileName}): {outcome.Error} after {outcome.Attempts} attempt(s)");
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

        foreach (var q in queued)
            _unzipQueue.Enqueue(q);

        return ScrapeResult.Ok("metadata.json", files);
    }

    /// <summary>
    /// Where MMF puts the download link(s) in /api/v2/objects/{id}. Verified live 2026-10-09:
    /// <c>archive_download_url</c> (top level) is set for some objects (e.g. purchased, multi-part)
    /// but is <c>null</c> for others; the per-file links always live in
    /// <c>files.items[].download_url</c> (/download/{id}?archive_id=…). #53: prefer the per-file
    /// links; the archive link can redirect to the HTML object page, so it is only a fallback.
    /// </summary>
    /// <summary>True when the object is in MMF's "PDF Only" category (slug/url "pdf").</summary>
    internal static bool IsPdfOnlyObject(JsonElement root)
    {
        if (!root.TryGetProperty("categories", out var c)) return false;
        var items = c.ValueKind == JsonValueKind.Object && c.TryGetProperty("items", out var it) ? it : c;
        if (items.ValueKind != JsonValueKind.Array) return false;
        foreach (var cat in items.EnumerateArray())
        {
            if (cat.ValueKind != JsonValueKind.Object) continue;
            var name = Str(cat, "name") ?? "";
            var url = Str(cat, "url") ?? Str(cat, "slug") ?? "";
            if (name.Contains("PDF", StringComparison.OrdinalIgnoreCase)
                || url.TrimEnd('/').EndsWith("/pdf", StringComparison.OrdinalIgnoreCase)
                || url.Equals("pdf", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>archive | document | image | model | other — recorded per file in metadata.json.</summary>
    internal static string ClassifyFileKind(string? fileName)
    {
        if (IsArchiveFile(fileName)) return "archive";
        return Path.GetExtension(fileName ?? "").ToLowerInvariant() switch
        {
            ".pdf" or ".epub" or ".txt" or ".md" or ".docx" => "document",
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" => "image",
            ".stl" or ".obj" or ".3mf" or ".lys" or ".ctb" or ".cbddlp" or ".gcode" or ".sl1" => "model",
            _ => "other",
        };
    }

    internal static List<MmfDownloadTarget> ResolveDownloadTargets(JsonElement root, string modelName)
    {
        var result = new List<MmfDownloadTarget>();
        // #53: per-file links (/download/{id}?archive_id=…) first. The bare archive link /download/{id}
        // 302s to the HTML object page for some objects (e.g. 802018), so it is only a fallback.
        AddFileItemTargets(root, modelName, result);
        if (result.Count > 0) return result;
        var archive = Str(root, "archive_download_url");
        if (archive is null && root.TryGetProperty("download_url", out var du) && du.ValueKind == JsonValueKind.String)
            archive = Str(root, "download_url");
        if (archive is not null)
            result.Add(new MmfDownloadTarget(Absolute(archive), $"{SanitizeFilename(modelName)}.zip", true, 0));
        return result;
    }

    private static void AddFileItemTargets(JsonElement root, string modelName, List<MmfDownloadTarget> result)
    {
        if (!root.TryGetProperty("files", out var filesEl)) return;
        var items = filesEl.ValueKind switch
        {
            JsonValueKind.Object when filesEl.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array => it,
            JsonValueKind.Array => filesEl,
            _ => default,
        };
        if (items.ValueKind != JsonValueKind.Array) return;

        var list = items.EnumerateArray()
            .Where(i => i.ValueKind == JsonValueKind.Object && Str(i, "download_url") is not null)
            .ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list)
        {
            var url = Absolute(Str(item, "download_url")!);
            var rawName = Str(item, "filename");
            var isArchive = rawName is null || rawName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || rawName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) || rawName.EndsWith(".rar", StringComparison.OrdinalIgnoreCase);
            // One archive file: keep the historical "<model>.zip" name so the on-disk skip check still matches.
            string name = list.Count == 1 && (rawName is null || rawName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ? $"{SanitizeFilename(modelName)}.zip"
                : SanitizeFilename(rawName ?? $"{SanitizeFilename(modelName)}-{result.Count + 1}.zip");
            var baseName = name; var n = 2;
            while (!used.Add(name))
                name = $"{Path.GetFileNameWithoutExtension(baseName)}-{n++}{Path.GetExtension(baseName)}";
            long size = 0;
            if (item.TryGetProperty("size", out var sz))
            {
                if (sz.ValueKind == JsonValueKind.Number) sz.TryGetInt64(out size);
                else if (sz.ValueKind == JsonValueKind.String) long.TryParse(sz.GetString(), out size);
            }
            result.Add(new MmfDownloadTarget(url, name, isArchive, size));
        }
    }

    /// <summary>Key names / counts only (never values) describing where files would live.</summary>
    internal static string DescribeFilesShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return "non-object";
        if (!root.TryGetProperty("files", out var f) || f.ValueKind == JsonValueKind.Null) return "files=absent";
        if (f.ValueKind == JsonValueKind.Array) return $"files=array[{f.GetArrayLength()}]";
        if (f.ValueKind != JsonValueKind.Object) return $"files={f.ValueKind}";
        var keys = string.Join(",", f.EnumerateObject().Select(p => p.Name));
        var items = f.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array ? it.GetArrayLength() : -1;
        var withUrl = items > 0 ? it.EnumerateArray().Count(i => i.ValueKind == JsonValueKind.Object && Str(i, "download_url") is not null) : 0;
        return $"files{{{keys}}} items={items} withDownloadUrl={withUrl}";
    }

    private static string Absolute(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? url : SiteBase + "/" + url.TrimStart('/');

    private static string? Str(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString() : null;
}

internal sealed record MmfDownloadTarget(string Url, string FileName, bool IsArchive, long ExpectedSize);
