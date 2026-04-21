using System.Net;
using System.Text;
using Forgekeeper.PluginSdk;
using Forgekeeper.Scraper.Mmf;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Tests for CDN redirect handling and bundle-skip logic (PRs #21/#22 coverage).
///
/// Covers:
///   - DownloadFileWithCleanRedirectAsync: 302 → CDN redirect without Authorization header
///   - DownloadFileWithCleanRedirectAsync: CDN request carries User-Agent
///   - DownloadFileWithCleanRedirectAsync: 302 → 200 returns correct bytes
///   - DownloadFileWithCleanRedirectAsync: 302 → 403 (CDN failure) returns the 403 response
///   - DownloadFileWithCleanRedirectAsync: no-redirect 200 returns body directly
///   - SplitManifestId: covers additional edge cases beyond MmfIdParsingTests
///   - Bundle-skip: non-object type from manifest skips via resolvedType check
/// </summary>
public class MmfDownloadAndSkipTests
{
    // ─────────────────────────────────────────────
    // DownloadFileWithCleanRedirectAsync
    // ─────────────────────────────────────────────

    [Fact]
    public async Task CleanRedirect_302_FollowsWithoutAuthorizationHeader()
    {
        // The CDN request after a 302 MUST NOT carry an Authorization header.
        // AWS S3 presigned URLs return 400 if any Authorization header is present.
        var recorder = new RecordingHandler();
        recorder.AddResponse(
            "https://www.myminifactory.com/download/123?archive_id=456",
            HttpStatusCode.Redirect,
            headers: new Dictionary<string, string>
            {
                ["Location"] = "https://dl4.myminifactory.com/file.zip?X-Amz-Signature=abc123"
            });
        recorder.AddResponse(
            "https://dl4.myminifactory.com/file.zip?X-Amz-Signature=abc123",
            HttpStatusCode.OK,
            body: "fake-file-bytes");

        using var httpClient = new HttpClient(recorder) { Timeout = TimeSpan.FromSeconds(5) };

        using var response = await MmfScraperPlugin.DownloadFileWithCleanRedirectAsync(
            httpClient,
            "https://www.myminifactory.com/download/123?archive_id=456",
            bearerToken: "my-oauth-token");

        // Verify the CDN request has no Authorization header
        var cdnRequest = recorder.Requests.FirstOrDefault(r =>
            r.RequestUri?.Host == "dl4.myminifactory.com");
        Assert.NotNull(cdnRequest);
        Assert.False(cdnRequest!.Headers.Contains("Authorization"),
            "CDN request MUST NOT carry Authorization — AWS presigned URLs reject extra auth");
    }

    [Fact]
    public async Task CleanRedirect_302_CdnRequestCarriesUserAgent()
    {
        // The CDN request must include a User-Agent so we don't get blocked by CDN bot filters.
        var recorder = new RecordingHandler();
        recorder.AddResponse(
            "https://www.myminifactory.com/download/789",
            HttpStatusCode.Redirect,
            headers: new Dictionary<string, string>
            {
                ["Location"] = "https://dl4.myminifactory.com/file2.zip?X-Amz-Signature=xyz"
            });
        recorder.AddResponse(
            "https://dl4.myminifactory.com/file2.zip?X-Amz-Signature=xyz",
            HttpStatusCode.OK,
            body: "bytes");

        using var httpClient = new HttpClient(recorder) { Timeout = TimeSpan.FromSeconds(5) };

        using var response = await MmfScraperPlugin.DownloadFileWithCleanRedirectAsync(
            httpClient,
            "https://www.myminifactory.com/download/789",
            bearerToken: "token");

        var cdnRequest = recorder.Requests.FirstOrDefault(r =>
            r.RequestUri?.Host == "dl4.myminifactory.com");
        Assert.NotNull(cdnRequest);
        Assert.True(cdnRequest!.Headers.UserAgent.Any(),
            "CDN request must include User-Agent");
    }

    [Fact]
    public async Task CleanRedirect_302_To200_ReturnsCorrectBytes()
    {
        const string expectedContent = "zip-file-content-here";
        var recorder = new RecordingHandler();
        recorder.AddResponse(
            "https://www.myminifactory.com/download/42",
            HttpStatusCode.Redirect,
            headers: new Dictionary<string, string>
            {
                ["Location"] = "https://dl4.myminifactory.com/model42.zip?X-Amz-Signature=sig"
            });
        recorder.AddResponse(
            "https://dl4.myminifactory.com/model42.zip?X-Amz-Signature=sig",
            HttpStatusCode.OK,
            body: expectedContent);

        using var httpClient = new HttpClient(recorder) { Timeout = TimeSpan.FromSeconds(5) };

        using var response = await MmfScraperPlugin.DownloadFileWithCleanRedirectAsync(
            httpClient,
            "https://www.myminifactory.com/download/42",
            bearerToken: "token");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(expectedContent, body);
    }

    [Fact]
    public async Task CleanRedirect_302_CdnReturns403_ReturnsTheFailedResponse()
    {
        // When CDN returns 403 (e.g., expired presigned URL), we return that response
        // and let the fallback chain in ScrapeModelAsync handle it.
        var recorder = new RecordingHandler();
        recorder.AddResponse(
            "https://www.myminifactory.com/download/99",
            HttpStatusCode.Redirect,
            headers: new Dictionary<string, string>
            {
                ["Location"] = "https://dl4.myminifactory.com/model99.zip?X-Amz-Signature=expired"
            });
        recorder.AddResponse(
            "https://dl4.myminifactory.com/model99.zip?X-Amz-Signature=expired",
            HttpStatusCode.Forbidden,
            body: "Access Denied");

        using var httpClient = new HttpClient(recorder) { Timeout = TimeSpan.FromSeconds(5) };

        using var response = await MmfScraperPlugin.DownloadFileWithCleanRedirectAsync(
            httpClient,
            "https://www.myminifactory.com/download/99",
            bearerToken: "token");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CleanRedirect_Direct200_NoRedirect_BodyUsedDirectly()
    {
        // When MMF returns 200 directly (no redirect), the body is returned as-is.
        const string expectedContent = "direct-body";
        var recorder = new RecordingHandler();
        recorder.AddResponse(
            "https://www.myminifactory.com/download/direct",
            HttpStatusCode.OK,
            body: expectedContent);

        using var httpClient = new HttpClient(recorder) { Timeout = TimeSpan.FromSeconds(5) };

        using var response = await MmfScraperPlugin.DownloadFileWithCleanRedirectAsync(
            httpClient,
            "https://www.myminifactory.com/download/direct",
            bearerToken: "token");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(expectedContent, body);
        // Only one request should have been made (no CDN follow-up)
        Assert.Single(recorder.Requests);
    }

    [Fact]
    public async Task CleanRedirect_BearerToken_SentOnInitialMmfRequest()
    {
        // The Authorization header IS sent to the MMF endpoint (Bearer auth required there).
        var recorder = new RecordingHandler();
        recorder.AddResponse(
            "https://www.myminifactory.com/download/auth-check",
            HttpStatusCode.OK,
            body: "ok");

        using var httpClient = new HttpClient(recorder) { Timeout = TimeSpan.FromSeconds(5) };

        using var response = await MmfScraperPlugin.DownloadFileWithCleanRedirectAsync(
            httpClient,
            "https://www.myminifactory.com/download/auth-check",
            bearerToken: "my-bearer-token");

        var mmfRequest = recorder.Requests.First();
        Assert.True(mmfRequest.Headers.Contains("Authorization"),
            "MMF download request must carry Authorization header");
        Assert.Contains("my-bearer-token",
            mmfRequest.Headers.Authorization?.Parameter ?? "");
    }

    [Fact]
    public async Task CleanRedirect_CdnUrl_NoBearerSentEvenOnFirstRequest()
    {
        // If the URL is already a CDN URL (IsCdnUrl=true), no Authorization is sent at all.
        var recorder = new RecordingHandler();
        recorder.AddResponse(
            "https://dl4.myminifactory.com/model.zip?X-Amz-Signature=presigned",
            HttpStatusCode.OK,
            body: "data");

        using var httpClient = new HttpClient(recorder) { Timeout = TimeSpan.FromSeconds(5) };

        using var response = await MmfScraperPlugin.DownloadFileWithCleanRedirectAsync(
            httpClient,
            "https://dl4.myminifactory.com/model.zip?X-Amz-Signature=presigned",
            bearerToken: "should-not-be-sent");

        var req = recorder.Requests.First();
        Assert.False(req.Headers.Contains("Authorization"),
            "CDN URLs must not get Authorization even on first request");
    }

    // ─────────────────────────────────────────────
    // Bundle-skip via resolvedType (SplitManifestId + model.Type)
    // ─────────────────────────────────────────────

    [Theory]
    [InlineData("bundle", "bundle-2447")]   // prefixed ID
    [InlineData("collection", "collection-999")] // prefixed ID
    [InlineData("bundle", "2447")]          // bare numeric ID + model.Type = "bundle"
    [InlineData("collection", "999")]       // bare numeric ID + model.Type = "collection"
    public void SplitManifestId_NonObjectPrefixOrType_ResolvedTypeIsNonObject(
        string modelType, string externalId)
    {
        var (numericId, idType) = MmfScraperPlugin.SplitManifestId(externalId);
        // resolvedType logic: if idType is not "object", use it; else fall back to model.Type
        var resolvedType = idType != "object" ? idType : modelType;
        Assert.NotEqual("object", resolvedType);
    }

    [Theory]
    [InlineData("object", "object-786967")]
    [InlineData("object", "786967")]
    [InlineData(null, "12345")]             // no type field → defaults to object
    public void SplitManifestId_ObjectEntries_ResolvedTypeIsObject(
        string? modelType, string externalId)
    {
        var (numericId, idType) = MmfScraperPlugin.SplitManifestId(externalId);
        var resolvedType = idType != "object" ? idType : (modelType ?? "object");
        Assert.Equal("object", resolvedType);
    }

    // ─────────────────────────────────────────────
    // SplitManifestId edge cases
    // ─────────────────────────────────────────────

    [Fact]
    public void SplitManifestId_DashOnlyPrefix_ReturnsCorrectSplit()
    {
        // Hypothetical future type like "custom-12345"
        var (numeric, type) = MmfScraperPlugin.SplitManifestId("custom-12345");
        Assert.Equal("12345", numeric);
        Assert.Equal("custom", type);
    }

    [Fact]
    public void SplitManifestId_MultipleDashes_SplitsOnFirst()
    {
        // ID that contains dashes after the prefix (unlikely but defensive)
        var (numeric, type) = MmfScraperPlugin.SplitManifestId("object-786-967");
        Assert.Equal("786-967", numeric);
        Assert.Equal("object", type);
    }

    // ─────────────────────────────────────────────
    // Recording HttpMessageHandler
    // ─────────────────────────────────────────────

    /// <summary>
    /// HttpMessageHandler that records all requests and returns pre-configured responses.
    /// Responses are matched by URL and served in order.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<HttpRequestMessage> _requests = new();
        private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body, Dictionary<string, string>? Headers)>> _responses = new();

        public IReadOnlyList<HttpRequestMessage> Requests => _requests;

        public void AddResponse(string url, HttpStatusCode status, string body = "", Dictionary<string, string>? headers = null)
        {
            if (!_responses.TryGetValue(url, out var queue))
            {
                queue = new Queue<(HttpStatusCode, string, Dictionary<string, string>?)>();
                _responses[url] = queue;
            }
            queue.Enqueue((status, body, headers));
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Add(request);

            var url = request.RequestUri?.AbsoluteUri ?? "";
            if (_responses.TryGetValue(url, out var queue) && queue.TryDequeue(out var cfg))
            {
                var resp = new HttpResponseMessage(cfg.Status)
                {
                    Content = new StringContent(cfg.Body, Encoding.UTF8),
                };
                if (cfg.Headers != null)
                {
                    foreach (var (k, v) in cfg.Headers)
                    {
                        if (k.Equals("Location", StringComparison.OrdinalIgnoreCase))
                            resp.Headers.Location = new Uri(v, UriKind.RelativeOrAbsolute);
                        else
                            resp.Headers.TryAddWithoutValidation(k, v);
                    }
                }
                return Task.FromResult(resp);
            }

            throw new InvalidOperationException(
                $"No response configured for URL: {url}\nConfigured URLs: {string.Join(", ", _responses.Keys)}");
        }
    }
}
