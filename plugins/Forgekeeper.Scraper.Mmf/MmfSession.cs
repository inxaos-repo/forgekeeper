using System.Net;
using System.Text;
using System.Text.Json;

namespace Forgekeeper.Scraper.Mmf;

/// <summary>
/// Phase 2: the saved MMF website session (cookies) is the primary credential.
/// Phase 0 proved it alone works for the library list, /api/v2/objects/{id} and the
/// /download/{id}?archive_id=… → file-storage redirect.
/// </summary>
public static class MmfSession
{
    /// <summary>Token-store key holding the normalized Cookie header (DbTokenStore encrypts at rest).</summary>
    public const string CookieKey = "session_cookies";
    public const string UserAgentKey = "session_useragent";
    public const string SavedAtKey = "session_saved_at";
    /// <summary>"ok" or "expired". Set to expired when MMF answers JSON 401/403 with the session.</summary>
    public const string StateKey = "session_state";

    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>
    /// Normalize what Damon pastes into a single Cookie header value. Accepts:
    ///  - a raw Cookie header ("a=1; b=2", optionally prefixed with "Cookie:"),
    ///  - a JSON array of cookie objects ({name,value,domain?}) — browser-extension export,
    ///  - a Playwright storageState object ({"cookies":[...]}).
    /// Cookies for non-MMF domains are dropped. Throws FormatException if nothing usable.
    /// </summary>
    public static string NormalizeCookieInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new FormatException("Empty cookie input");
        var text = input.Trim();
        var pairs = new List<KeyValuePair<string, string>>();

        if (text.StartsWith('[') || text.StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(text);
            var arr = doc.RootElement;
            if (arr.ValueKind == JsonValueKind.Object && arr.TryGetProperty("cookies", out var c)) arr = c;
            if (arr.ValueKind != JsonValueKind.Array) throw new FormatException("JSON must be an array of cookies or {\"cookies\":[...]}");
            foreach (var el in arr.EnumerateArray())
            {
                var name = el.TryGetProperty("name", out var n) ? n.GetString() : null;
                var value = el.TryGetProperty("value", out var v) ? v.GetString() : null;
                var domain = el.TryGetProperty("domain", out var d) ? d.GetString() : null;
                if (string.IsNullOrEmpty(name) || value is null) continue;
                if (!string.IsNullOrEmpty(domain) && !domain.TrimStart('.').EndsWith("myminifactory.com", StringComparison.OrdinalIgnoreCase)) continue;
                pairs.Add(new(name, value));
            }
        }
        else
        {
            if (text.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase)) text = text[7..];
            foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                pairs.Add(new(part[..eq].Trim(), part[(eq + 1)..].Trim()));
            }
        }

        // Last value wins per name; keep first-seen order.
        var order = new List<string>();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs)
        {
            if (!map.ContainsKey(k)) order.Add(k);
            map[k] = v;
        }
        if (order.Count == 0) throw new FormatException("No cookies found in input");
        return string.Join("; ", order.Select(k => $"{k}={map[k]}"));
    }

    /// <summary>Cookie names only — safe to log/return (values are secrets).</summary>
    public static IReadOnlyList<string> CookieNames(string cookieHeader) =>
        cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2)[0]).ToList();

    /// <summary>Only MMF hosts may receive the session cookie / bearer (never the file-storage redirect target).</summary>
    public static bool IsMmfHost(Uri uri) =>
        uri.Host.Equals("myminifactory.com", StringComparison.OrdinalIgnoreCase)
        || uri.Host.EndsWith(".myminifactory.com", StringComparison.OrdinalIgnoreCase);

    public static MmfResponseKind Classify(HttpStatusCode status, string? contentType, string? bodyPrefix)
    {
        var code = (int)status;
        if (code is >= 200 and < 300)
            return LooksHtml(contentType, bodyPrefix) && bodyPrefix != null && bodyPrefix.Contains("challenge", StringComparison.OrdinalIgnoreCase)
                ? MmfResponseKind.CloudflareChallenge : MmfResponseKind.Ok;
        if (status == HttpStatusCode.NotFound) return MmfResponseKind.NotFound;
        if (status == HttpStatusCode.TooManyRequests) return MmfResponseKind.RateLimited;
        // Phase 0: HTML 403 = Cloudflare challenge; JSON 401/403 = auth expired.
        if (status is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable && LooksHtml(contentType, bodyPrefix))
            return MmfResponseKind.CloudflareChallenge;
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return MmfResponseKind.AuthExpired;
        if (code >= 500) return MmfResponseKind.ServerError;
        return MmfResponseKind.Other;
    }

    /// <summary>#48: Cloudflare marks challenge responses with <c>cf-mitigated: challenge</c>, whatever the status.</summary>
    public static bool HasChallengeHeader(HttpResponseMessage resp) =>
        resp.Headers.TryGetValues("cf-mitigated", out var v) && v.Any(x => x.Contains("challenge", StringComparison.OrdinalIgnoreCase));

    private static readonly string[] ChallengeMarkers =
    {
        "challenge-platform", "Just a moment", "cf-chl", "_cf_chl_opt", "cf_chl_", "cf-browser-verification",
        "Attention Required! | Cloudflare", "Checking your browser", "challenges.cloudflare.com", "cf-turnstile",
    };

    /// <summary>#48: body markers of a Cloudflare interstitial / managed challenge page.</summary>
    public static bool LooksLikeChallengeBody(string? body) =>
        !string.IsNullOrEmpty(body) && ChallengeMarkers.Any(m => body.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// #48: content types a file download may legitimately carry. Anything else (text/*, JSON, XML)
    /// on a download URL is suspicious and gets its body inspected before being trusted.
    /// </summary>
    public static bool IsExpectedDownloadContentType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType)) return true;
        var m = mediaType.Trim().ToLowerInvariant();
        return m is "application/octet-stream" or "binary/octet-stream" or "application/x-binary" or "application/binary"
                 or "application/zip" or "application/x-zip-compressed" or "application/x-zip" or "multipart/x-zip"
                 or "application/x-7z-compressed" or "application/x-rar-compressed" or "application/vnd.rar" or "application/x-rar"
                 or "application/pdf" or "application/x-pdf" or "application/sla" or "application/vnd.ms-pki.stl"
                 or "application/x-tar" or "application/gzip" or "application/x-gzip" or "application/force-download"
                 or "application/download" or "application/x-download"
            || m.StartsWith("image/") || m.StartsWith("model/");
    }

    internal static bool LooksHtmlPublic(string? contentType, string? body) => LooksHtml(contentType, body);

    private static bool LooksHtml(string? contentType, string? body) =>
        (contentType?.Contains("html", StringComparison.OrdinalIgnoreCase) ?? false)
        || (body?.TrimStart().StartsWith('<') ?? false);
}

public enum MmfResponseKind { Ok, NotFound, RateLimited, CloudflareChallenge, AuthExpired, ServerError, Other }

/// <summary>Credentials for one request chain: session cookies first, OAuth bearer as fallback.</summary>
public sealed record MmfCredentials(string? CookieHeader, string? BearerToken, string UserAgent)
{
    /// <summary>FETCH_PROXY_URL (curl_cffi sidecar) or null for direct requests.</summary>
    public string? FetchProxyUrl { get; init; }

    public bool HasSession => !string.IsNullOrEmpty(CookieHeader);
    public bool HasBearer => !string.IsNullOrEmpty(BearerToken);

    public void Apply(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (req.RequestUri is null || !req.RequestUri.IsAbsoluteUri || !MmfSession.IsMmfHost(req.RequestUri)) return;
        if (HasSession) req.Headers.TryAddWithoutValidation("Cookie", CookieHeader);
        if (HasBearer) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", BearerToken);
    }
}

