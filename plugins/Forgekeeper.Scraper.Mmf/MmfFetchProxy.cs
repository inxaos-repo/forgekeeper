using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace Forgekeeper.Scraper.Mmf;

/// <summary>
/// Opt-in curl_cffi fetch sidecar (FETCH_PROXY_URL, e.g. http://127.0.0.1:8199).
/// .NET HttpClient can't present a browser TLS/HTTP2 fingerprint, so Cloudflare answers
/// 403 cf-mitigated even with valid cookies. When FETCH_PROXY_URL is set every request to
/// *.myminifactory.com is re-addressed to <c>{proxy}/fetch</c> with the real target in
/// <c>X-Fetch-Url</c>; the sidecar replays it with impersonate=chrome and streams back the
/// upstream status, headers and body. Blank = off (behaviour unchanged).
/// </summary>
public static class MmfFetchProxy
{
    public const string ConfigKey = "FETCH_PROXY_URL";
    public const string TargetHeader = "X-Fetch-Url";
    public const string ErrorHeader = "X-Fetch-Proxy-Error";

    private static readonly ConcurrentDictionary<string, HttpMessageHandler> Cache = new();

    public static string? GetUrl(IReadOnlyDictionary<string, string>? config) =>
        config != null && config.TryGetValue(ConfigKey, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v.Trim().TrimEnd('/') : null;

    /// <summary>
    /// Wraps <paramref name="inner"/> with the sidecar handler when FETCH_PROXY_URL is set,
    /// otherwise returns <paramref name="inner"/> unchanged.
    /// </summary>
    public static HttpMessageHandler Wrap(IReadOnlyDictionary<string, string>? config, HttpMessageHandler inner, bool followRedirects = false)
    {
        var url = GetUrl(config);
        return url == null ? inner : new MmfFetchProxyHandler(new Uri(url), followRedirects) { InnerHandler = inner };
    }

    /// <summary>Process-wide shared proxy handler (no redirects, no cookies) for a given proxy URL.</summary>
    public static HttpMessageHandler? Shared(IReadOnlyDictionary<string, string>? config)
    {
        var url = GetUrl(config);
        if (url == null) return null;
        return Cache.GetOrAdd(url, u => new MmfFetchProxyHandler(new Uri(u), followRedirects: false)
        {
            InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All },
        });
    }

    /// <summary>A client for one-off calls (e.g. the token/session check) — through the sidecar when configured.</summary>
    public static HttpClient? CreateClient(IReadOnlyDictionary<string, string>? config, TimeSpan timeout)
    {
        var h = Shared(config);
        return h == null ? null : new HttpClient(h, disposeHandler: false) { Timeout = timeout };
    }
}

/// <summary>
/// Re-addresses MMF requests to the fetch sidecar. Non-MMF hosts (e.g. a storage CDN after a
/// redirect) go straight through the inner handler — the sidecar only allows *.myminifactory.com.
/// </summary>
public sealed class MmfFetchProxyHandler(Uri proxyBase, bool followRedirects = false) : DelegatingHandler
{
    private const int MaxRedirects = 8;
    private readonly Uri _fetchUri = new(proxyBase.ToString().TrimEnd('/') + "/fetch");

    public Uri FetchUri => _fetchUri;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!followRedirects) return await SendOnceAsync(request, ct);

        var current = request;
        for (int hop = 0; ; hop++)
        {
            var resp = await SendOnceAsync(current, ct);
            var code = (int)resp.StatusCode;
            if (code is not (301 or 302 or 303 or 307 or 308) || resp.Headers.Location is not { } loc || hop >= MaxRedirects)
                return resp;
            var next = loc.IsAbsoluteUri ? loc : new Uri(current.RequestUri!, loc);
            resp.Dispose();
            var method = code == 303 || (code is 301 or 302 && current.Method == HttpMethod.Post) ? HttpMethod.Get : current.Method;
            var nextReq = new HttpRequestMessage(method, next);
            foreach (var h in current.Headers)
            {
                // Never carry credentials off MMF.
                if (!MmfSession.IsMmfHost(next) && (h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                    || h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))) continue;
                nextReq.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            if (!ReferenceEquals(current, request)) current.Dispose();
            current = nextReq;
        }
    }

    private Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var target = request.RequestUri;
        if (target == null || !target.IsAbsoluteUri || !MmfSession.IsMmfHost(target))
            return base.SendAsync(request, ct);

        var fwd = new HttpRequestMessage(request.Method, _fetchUri) { Version = HttpVersion.Version11, Content = request.Content };
        foreach (var h in request.Headers)
        {
            if (h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            fwd.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        fwd.Headers.Remove(MmfFetchProxy.TargetHeader);
        fwd.Headers.TryAddWithoutValidation(MmfFetchProxy.TargetHeader, target.AbsoluteUri);
        return SendAndRestoreAsync(fwd, request, ct);
    }

    private async Task<HttpResponseMessage> SendAndRestoreAsync(HttpRequestMessage fwd, HttpRequestMessage original, CancellationToken ct)
    {
        var resp = await base.SendAsync(fwd, ct);
        // Callers resolve relative Location headers against RequestMessage.RequestUri.
        resp.RequestMessage = original;
        if (resp.Headers.Contains(MmfFetchProxy.ErrorHeader))
            resp.ReasonPhrase = "Fetch proxy error";
        return resp;
    }
}
