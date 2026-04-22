using System.Net;
using System.Net.Http.Headers;
using Forgekeeper.Scraper.Mmf;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Unit tests for the Cloudflare HTML challenge detection helper and the download
/// fallback classification logic.
///
/// Playwright paths are NOT unit-tested here (require a browser); they are verified
/// by integration testing post-deploy.
/// </summary>
public class MmfCfDetectionTests
{
    // ── IsCfHtmlChallenge ─────────────────────────────────────────────────────

    [Fact]
    public void IsCfHtmlChallenge_Returns_True_For_403_HtmlBody()
    {
        var response = MakeResponse(HttpStatusCode.Forbidden, "text/html");
        var body = "<!DOCTYPE html><html><head><title>Just a moment...</title></head><body>Cloudflare challenge</body></html>";

        Assert.True(MmfScraperPlugin.IsCfHtmlChallenge(response, body));
    }

    [Fact]
    public void IsCfHtmlChallenge_Returns_True_For_503_HtmlBody()
    {
        var response = MakeResponse(HttpStatusCode.ServiceUnavailable, "text/html");
        var body = "<html><body>Service Temporarily Unavailable</body></html>";

        Assert.True(MmfScraperPlugin.IsCfHtmlChallenge(response, body));
    }

    [Fact]
    public void IsCfHtmlChallenge_Returns_False_For_401_JsonBody()
    {
        var response = MakeResponse(HttpStatusCode.Unauthorized, "application/json");
        var body = "{\"error\": \"Unauthorized\", \"message\": \"Invalid token\"}";

        Assert.False(MmfScraperPlugin.IsCfHtmlChallenge(response, body));
    }

    [Fact]
    public void IsCfHtmlChallenge_Returns_False_For_200_JsonBody()
    {
        var response = MakeResponse(HttpStatusCode.OK, "application/json");
        var body = "{\"id\": 12345, \"name\": \"Cool Model\"}";

        Assert.False(MmfScraperPlugin.IsCfHtmlChallenge(response, body));
    }

    [Fact]
    public void IsCfHtmlChallenge_Returns_False_For_403_JsonBody()
    {
        // 403 with a JSON body is a legit API error (auth failure), NOT a CF challenge
        var response = MakeResponse(HttpStatusCode.Forbidden, "application/json");
        var body = "{\"error\": 403, \"message\": \"Access denied\"}";

        Assert.False(MmfScraperPlugin.IsCfHtmlChallenge(response, body));
    }

    [Fact]
    public void IsCfHtmlChallenge_Returns_False_For_403_EmptyBody()
    {
        var response = MakeResponse(HttpStatusCode.Forbidden, "text/html");
        var body = "";

        Assert.False(MmfScraperPlugin.IsCfHtmlChallenge(response, body));
    }

    [Fact]
    public void IsCfHtmlChallenge_Returns_False_For_404_HtmlBody()
    {
        // 404 HTML is a real "not found" page, not a CF challenge
        var response = MakeResponse(HttpStatusCode.NotFound, "text/html");
        var body = "<html><body>Not Found</body></html>";

        Assert.False(MmfScraperPlugin.IsCfHtmlChallenge(response, body));
    }

    // ── Download fallback classification ──────────────────────────────────────
    //
    // The main download handler classifies response status+body and routes to the
    // correct fallback. These tests encode the decision table:
    //
    //   302          → direct CDN path (success, no fallback needed)
    //   200          → direct download (success, no fallback needed)
    //   403 + HTML   → CF HTML challenge → Playwright (skip cookies, same TLS issue)
    //   401 + JSON   → auth error → surface to user (don't fall back)
    //   403 + JSON   → non-CF 403 → try cookies first, then Playwright

    [Theory]
    [InlineData(302, "text/html", "<html>redirect</html>", false)]  // 302 redirect → NOT a CF challenge (success path)
    [InlineData(200, "application/json", "{\"id\":1}", false)]        // 200 JSON → success, no challenge
    [InlineData(403, "text/html", "<!DOCTYPE html><html>", true)]    // 403 HTML → CF challenge → Playwright
    [InlineData(403, "text/html", "<html>Just a moment", true)]      // 403 HTML lowercase → CF challenge
    [InlineData(401, "application/json", "{\"error\":\"Unauthorized\"}", false)]  // 401 JSON → auth error, not CF
    [InlineData(403, "application/json", "{\"error\":\"Forbidden\"}", false)]     // 403 JSON → non-CF 403
    [InlineData(503, "text/html", "<html>Service Unavailable", true)] // 503 HTML → CF challenge
    public void DownloadFallbackClassification_IsCfHtmlChallenge(
        int statusCode, string contentType, string body, bool expectedIsCf)
    {
        var response = MakeResponse((HttpStatusCode)statusCode, contentType);
        var result = MmfScraperPlugin.IsCfHtmlChallenge(response, body);
        Assert.Equal(expectedIsCf, result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpResponseMessage MakeResponse(HttpStatusCode status, string contentType)
    {
        var response = new HttpResponseMessage(status);
        response.Content = new StringContent("", System.Text.Encoding.UTF8, contentType);
        return response;
    }
}
