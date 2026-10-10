using System.IO.Compression;
using System.Net;
using System.Text;
using Forgekeeper.Infrastructure.Services;
using Forgekeeper.PluginSdk;
using Forgekeeper.Scraper.Mmf;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>FETCH_PROXY_URL (curl_cffi sidecar) routing. No real MMF calls.</summary>
[Collection("MmfApiHandlerOverride")]
public class MmfFetchProxyTests : IDisposable
{
    private const string Proxy = "http://127.0.0.1:8199";
    private readonly string _dir = Directory.CreateTempSubdirectory("fk-fp-").FullName;

    public MmfFetchProxyTests()
    {
        MmfScraperPlugin.DelayOverride = (_, _) => Task.CompletedTask;
    }

    public void Dispose()
    {
        MmfScraperPlugin.ApiHandlerOverride = null;
        MmfScraperPlugin.DelayOverride = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static string? Target(HttpRequestMessage r) =>
        r.Headers.TryGetValues(MmfFetchProxy.TargetHeader, out var v) ? v.Single() : null;

    private static string? Header(HttpRequestMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? string.Join(";", v) : null;

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static byte[] Zip()
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        using (var s = z.CreateEntry("p.stl").Open()) s.Write(Encoding.ASCII.GetBytes("solid x\nendsolid x\n"));
        return ms.ToArray();
    }

    private PluginContext Ctx(ITokenStore tokens, Dictionary<string, string> config, HttpMessageHandler? ctxHandler = null) => new()
    {
        SourceDirectory = _dir,
        ModelDirectory = Path.Combine(_dir, "creator", "model"),
        Config = config,
        HttpClient = new HttpClient(ctxHandler ?? new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
        Logger = NullLogger.Instance,
        TokenStore = tokens,
        Progress = new Progress<ScrapeProgress>(),
    };

    private static async Task<InMemoryTokenStore> SessionTokens()
    {
        var t = new InMemoryTokenStore();
        await t.SaveTokenAsync(MmfSession.CookieKey, "PHPSESSID=keepme; cf_clearance=abc");
        await t.SaveTokenAsync(MmfSession.StateKey, "ok");
        await t.SaveTokenAsync(MmfSession.UserAgentKey, "Damon-UA/1.0");
        return t;
    }

    private static ScrapedModel Model() => new() { ExternalId = "200", Name = "Cool Dragon", CreatorName = "creator" };

    private const string ObjectJson =
        "{\"id\":200,\"name\":\"Cool Dragon\",\"archive_download_url\":\"https://www.myminifactory.com/download/200?archive_id=9\"}";

    [Fact]
    public void FetchProxyUrl_IsDeclaredOptInSetting()
    {
        var f = new MmfScraperPlugin().ConfigSchema.Single(f => f.Key == "FETCH_PROXY_URL");
        Assert.Equal("", f.DefaultValue);
        Assert.False(f.Required);
        Assert.Equal(PluginConfigFieldType.Url, f.Type);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("http://127.0.0.1:8199/", "http://127.0.0.1:8199")]
    public void GetUrl_BlankMeansOff(string? value, string? expected)
    {
        var cfg = value == null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["FETCH_PROXY_URL"] = value };
        Assert.Equal(expected, MmfFetchProxy.GetUrl(cfg));
    }

    [Fact]
    public void Wrap_WithoutSetting_ReturnsInnerUnchanged()
    {
        var inner = new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        Assert.Same(inner, MmfFetchProxy.Wrap(new Dictionary<string, string>(), inner));
        Assert.Null(MmfFetchProxy.CreateClient(new Dictionary<string, string>(), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Handler_ReaddressesMmfRequest_ToSidecar_WithHeaders()
    {
        var inner = new RouteHandler(_ => Json(HttpStatusCode.OK, "{}"));
        using var client = new HttpClient(new MmfFetchProxyHandler(new Uri(Proxy)) { InnerHandler = inner });
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.myminifactory.com/api/v2/objects/1?x=1");
        req.Headers.TryAddWithoutValidation("Cookie", "PHPSESSID=s");
        req.Headers.TryAddWithoutValidation("User-Agent", "UA/1");
        using var resp = await client.SendAsync(req);

        var sent = Assert.Single(inner.Requests);
        Assert.Equal(Proxy + "/fetch", sent.RequestUri!.ToString());
        Assert.Equal("https://www.myminifactory.com/api/v2/objects/1?x=1", Target(sent));
        Assert.Equal("PHPSESSID=s", Header(sent, "Cookie"));
        Assert.Contains("UA/1", Header(sent, "User-Agent"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // Callers resolving relative Location headers still see the original target.
        Assert.Equal("www.myminifactory.com", resp.RequestMessage!.RequestUri!.Host);
    }

    [Fact]
    public async Task Handler_HeadMethod_IsPreserved()
    {
        var inner = new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new MmfFetchProxyHandler(new Uri(Proxy)) { InnerHandler = inner });
        using var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "https://dl.myminifactory.com/f.zip"));
        Assert.Equal(HttpMethod.Head, inner.Requests.Single().Method);
    }

    [Fact]
    public async Task Handler_NonMmfHost_GoesDirect()
    {
        var inner = new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new MmfFetchProxyHandler(new Uri(Proxy)) { InnerHandler = inner });
        await client.GetAsync("https://cdn.example.net/file.zip");
        var sent = inner.Requests.Single();
        Assert.Equal("cdn.example.net", sent.RequestUri!.Host);
        Assert.Null(Target(sent));
    }

    [Fact]
    public async Task Handler_DoesNotFollowRedirects_ByDefault()
    {
        var inner = new RouteHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://cdn.example.net/f.zip");
            return r;
        });
        using var client = new HttpClient(new MmfFetchProxyHandler(new Uri(Proxy)) { InnerHandler = inner });
        using var resp = await client.GetAsync("https://www.myminifactory.com/download/1");
        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task Handler_FollowRedirects_ReproxiesMmf_AndStripsCredsOffMmf()
    {
        var inner = new RouteHandler(req =>
        {
            var t = Target(req) ?? req.RequestUri!.ToString();
            if (t.EndsWith("/download/1"))
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("/download/1b", UriKind.Relative);
                return r;
            }
            if (t.EndsWith("/download/1b"))
            {
                var r = new HttpResponseMessage(HttpStatusCode.Redirect);
                r.Headers.Location = new Uri("https://cdn.example.net/f.zip");
                return r;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(new MmfFetchProxyHandler(new Uri(Proxy), followRedirects: true) { InnerHandler = inner });
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.myminifactory.com/download/1");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer t");
        using var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(3, inner.Requests.Count);
        Assert.Equal("https://www.myminifactory.com/download/1b", Target(inner.Requests[1]));
        Assert.NotNull(Header(inner.Requests[1], "Authorization"));
        Assert.Equal("cdn.example.net", inner.Requests[2].RequestUri!.Host);
        Assert.Null(Header(inner.Requests[2], "Authorization"));
    }

    [Fact]
    public async Task SessionScrape_WithProxy_RoutesApiAndDownload_ThroughSidecar()
    {
        var tokens = await SessionTokens();
        var handler = new RouteHandler(req =>
        {
            var t = Target(req) ?? "";
            if (t.Contains("/api/v2/objects/200")) return Json(HttpStatusCode.OK, ObjectJson);
            if (t.Contains("/download/200"))
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip()) };
                r.Content.Headers.ContentType = new("application/zip");
                return r;
            }
            return Json(HttpStatusCode.NotFound, "{}");
        });
        MmfScraperPlugin.ApiHandlerOverride = handler;
        var r = await new MmfScraperPlugin().ScrapeModelAsync(
            Ctx(tokens, new() { ["DELAY_MS"] = "0", ["FETCH_PROXY_URL"] = Proxy }), Model());

        Assert.True(r.Success, r.Error);
        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, q => Assert.Equal(Proxy + "/fetch", q.RequestUri!.ToString()));
        Assert.Contains(handler.Requests, q => Target(q)!.Contains("/api/v2/objects/200"));
        Assert.Contains(handler.Requests, q => Target(q)!.Contains("/download/200"));
        Assert.All(handler.Requests, q => Assert.Contains("PHPSESSID=keepme", Header(q, "Cookie")));
    }

    [Fact]
    public async Task SessionScrape_WithoutProxy_IsUnchanged_DirectToMmf()
    {
        var tokens = await SessionTokens();
        var handler = new RouteHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/api/v2/objects/200")) return Json(HttpStatusCode.OK, ObjectJson);
            if (url.Contains("/download/200"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip()) };
            return Json(HttpStatusCode.NotFound, "{}");
        });
        MmfScraperPlugin.ApiHandlerOverride = handler;
        var r = await new MmfScraperPlugin().ScrapeModelAsync(Ctx(tokens, new() { ["DELAY_MS"] = "0" }), Model());

        Assert.True(r.Success, r.Error);
        Assert.All(handler.Requests, q => Assert.Equal("www.myminifactory.com", q.RequestUri!.Host));
        Assert.All(handler.Requests, q => Assert.Null(Target(q)));
    }

    [Fact]
    public async Task SessionScrape_WithProxy_ChallengeStillDetected()
    {
        var tokens = await SessionTokens();
        MmfScraperPlugin.ApiHandlerOverride = new RouteHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<html><title>Just a moment...</title></html>", Encoding.UTF8, "text/html"),
            };
            r.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
            return r;
        });
        var r = await new MmfScraperPlugin().ScrapeModelAsync(
            Ctx(tokens, new() { ["DELAY_MS"] = "0", ["FETCH_PROXY_URL"] = Proxy }), Model());
        Assert.False(r.Success);
        Assert.True(MmfScraperPlugin.IsChallengeError(r.Error), r.Error);
    }
}
