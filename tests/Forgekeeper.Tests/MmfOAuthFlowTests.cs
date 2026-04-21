using System.Net;
using System.Text;
using Forgekeeper.PluginSdk;
using Forgekeeper.Scraper.Mmf;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Tests for the MMF OAuth 2.0 implicit flow.
///
/// Covers:
///   - Config schema: CLIENT_SECRET field exists, is Optional, is Secret type
///   - No MMF_API_KEY field in schema (removed as dead)
///   - AuthenticateAsync — live /api/v2/user check (4 scenarios via HttpMessageHandler stub)
///   - AuthenticateAsync — returns NeedsBrowser when OAuth configured + no token
///   - AuthenticateAsync — returns Failed when credentials missing
///   - OAuth authorize URL shape: endpoint, response_type, no client_secret, state CSRF guard,
///     redirect_uri URL-encoded
///   - HandleAuthCallbackAsync — implicit-flow-only paths (success / error / empty)
/// </summary>
public class MmfOAuthFlowTests
{
    // ─────────────────────────────────────────────
    // Config schema tests
    // ─────────────────────────────────────────────

    [Fact]
    public void ConfigSchema_HasClientSecretField_OfSecretType()
    {
        var plugin = new MmfScraperPlugin();
        var field = plugin.ConfigSchema.FirstOrDefault(f => f.Key == "CLIENT_SECRET");

        Assert.NotNull(field);
        Assert.Equal(PluginConfigFieldType.Secret, field!.Type);
    }

    [Fact]
    public void ConfigSchema_ClientSecret_IsOptional()
    {
        // CLIENT_SECRET is not Required — manifest-only sync works without it.
        var plugin = new MmfScraperPlugin();
        var field = plugin.ConfigSchema.FirstOrDefault(f => f.Key == "CLIENT_SECRET");

        Assert.NotNull(field);
        Assert.False(field!.Required,
            "CLIENT_SECRET must be optional so manifest-only sync works without registering an OAuth app.");
    }

    [Fact]
    public void ConfigSchema_DoesNotHaveMmfApiKeyField()
    {
        // MMF_API_KEY was empirically dead (developer-portal API keys 401 on /api/v2).
        // Removed to avoid confusion; existing DB rows are silently ignored.
        var plugin = new MmfScraperPlugin();
        var field = plugin.ConfigSchema.FirstOrDefault(f => f.Key == "MMF_API_KEY");
        Assert.Null(field);
    }

    [Fact]
    public void ConfigSchema_ClientId_HelpText_MentionsClientSecret()
    {
        var plugin = new MmfScraperPlugin();
        var field = plugin.ConfigSchema.FirstOrDefault(f => f.Key == "CLIENT_ID");

        Assert.NotNull(field);
        Assert.Contains("CLIENT_SECRET", field!.HelpText, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────
    // AuthenticateAsync — missing credentials
    // ─────────────────────────────────────────────

    [Fact]
    public async Task AuthenticateAsync_WithNoCredentials_ReturnsFailed()
    {
        var plugin = new MmfScraperPlugin();
        var context = BuildContext(config: new Dictionary<string, string>());

        var result = await plugin.AuthenticateAsync(context);

        Assert.False(result.Authenticated);
        Assert.Null(result.AuthUrl);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task AuthenticateAsync_WithCredentialsOnly_ReturnsSuccess()
    {
        // Username+password but no OAuth → manifest-only mode (still Authenticated=true)
        var plugin = new MmfScraperPlugin();
        var context = BuildContext(config: new Dictionary<string, string>
        {
            ["MMF_USERNAME"] = "user@example.com",
            ["MMF_PASSWORD"] = "hunter2",
        });

        var result = await plugin.AuthenticateAsync(context);

        Assert.True(result.Authenticated);
        Assert.Null(result.AuthUrl);
    }

    // ─────────────────────────────────────────────
    // AuthenticateAsync — live /api/v2/user check
    // ─────────────────────────────────────────────

    [Fact]
    public async Task AuthenticateAsync_TokenInStore_UserEndpointReturns200_ReturnsSuccess()
    {
        // Token exists, /api/v2/user returns 200 → plugin trusts it and returns Success
        var tokenStore = new InMemoryTokenStore();
        await tokenStore.SaveTokenAsync("access_token", "valid-token");

        var handler = new StaticResponseHandler(
            new Uri("https://www.myminifactory.com/api/v2/user"),
            HttpStatusCode.OK,
            "{\"username\":\"testuser\"}");

        var plugin = new MmfScraperPlugin();
        var context = BuildContext(
            config: OAuthConfig(),
            tokenStore: tokenStore,
            httpClient: new HttpClient(handler));

        var result = await plugin.AuthenticateAsync(context);

        Assert.True(result.Authenticated);
        Assert.Null(result.AuthUrl);
        Assert.Contains("verified", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthenticateAsync_TokenInStore_UserEndpointReturns401_ReturnsNeedsBrowser()
    {
        // Stored token failed verification → force re-auth
        var tokenStore = new InMemoryTokenStore();
        await tokenStore.SaveTokenAsync("access_token", "stale-token");

        var handler = new StaticResponseHandler(
            new Uri("https://www.myminifactory.com/api/v2/user"),
            HttpStatusCode.Unauthorized,
            "{\"error\":\"invalid_token\"}");

        var plugin = new MmfScraperPlugin();
        var context = BuildContext(
            config: OAuthConfig(),
            tokenStore: tokenStore,
            httpClient: new HttpClient(handler));

        var result = await plugin.AuthenticateAsync(context);

        Assert.False(result.Authenticated);
        Assert.NotNull(result.AuthUrl);
        Assert.Contains("auth.myminifactory.com/web/authorize", result.AuthUrl);
    }

    [Fact]
    public async Task AuthenticateAsync_NoTokenInStore_ReturnsNeedsBrowser()
    {
        // No stored token at all → trigger browser auth
        var handler = new StaticResponseHandler(
            new Uri("https://www.myminifactory.com/api/v2/user"),
            HttpStatusCode.OK, "{}"); // Should never be called

        var plugin = new MmfScraperPlugin();
        var context = BuildContext(
            config: OAuthConfig(),
            tokenStore: new InMemoryTokenStore(),
            httpClient: new HttpClient(handler));

        var result = await plugin.AuthenticateAsync(context);

        Assert.False(result.Authenticated);
        Assert.NotNull(result.AuthUrl);
        Assert.Contains("auth.myminifactory.com/web/authorize", result.AuthUrl);
        // The /api/v2/user call should NOT have been made (no token to verify)
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task AuthenticateAsync_HttpClientThrows_GracefullyReturnsNeedsBrowser()
    {
        // Network error during live check → degrade gracefully, don't throw
        var tokenStore = new InMemoryTokenStore();
        await tokenStore.SaveTokenAsync("access_token", "maybe-valid-token");

        var handler = new ThrowingHandler(new HttpRequestException("Network unreachable"));

        var plugin = new MmfScraperPlugin();
        var context = BuildContext(
            config: OAuthConfig(),
            tokenStore: tokenStore,
            httpClient: new HttpClient(handler));

        var result = await plugin.AuthenticateAsync(context);

        // Must not throw; should fall back to NeedsBrowser
        Assert.False(result.Authenticated);
        Assert.NotNull(result.AuthUrl);
    }

    // ─────────────────────────────────────────────
    // AuthenticateAsync — OAuth authorize URL shape
    // ─────────────────────────────────────────────

    [Fact]
    public async Task AuthenticateAsync_WithOAuthConfig_AndNoToken_ReturnsNeedsBrowser()
    {
        var plugin = new MmfScraperPlugin();
        var context = BuildContext(
            config: OAuthConfig(),
            tokenStore: new InMemoryTokenStore(),
            httpClient: new HttpClient(new StaticResponseHandler(
                new Uri("https://www.myminifactory.com/api/v2/user"),
                HttpStatusCode.OK, "{}")));

        var result = await plugin.AuthenticateAsync(context);

        Assert.False(result.Authenticated);
        Assert.NotNull(result.AuthUrl);
        Assert.Contains("auth.myminifactory.com/web/authorize", result.AuthUrl);
        Assert.Contains("response_type=token", result.AuthUrl);
        Assert.Contains("client_id=", result.AuthUrl);
        Assert.Contains("redirect_uri=", result.AuthUrl);
    }

    [Fact]
    public async Task AuthenticateAsync_AuthUrl_DoesNotContainClientSecret()
    {
        // Implicit flow: client_secret MUST NOT appear in the authorize URL
        // (it's a client-side flow; the secret is only used on the token endpoint which we don't call)
        var plugin = new MmfScraperPlugin();
        var context = BuildContext(
            config: OAuthConfig(),
            tokenStore: new InMemoryTokenStore(),
            httpClient: new HttpClient(new NoCallHandler()));

        var result = await plugin.AuthenticateAsync(context);

        Assert.NotNull(result.AuthUrl);
        Assert.DoesNotContain("client_secret", result.AuthUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("6b511607", result.AuthUrl); // the actual public secret value
    }

    [Fact]
    public async Task AuthenticateAsync_AuthUrl_HasStateParam_AtLeast16Chars()
    {
        // state is a CSRF guard; must be present and long enough to be unpredictable
        var plugin = new MmfScraperPlugin();
        var context = BuildContext(
            config: OAuthConfig(),
            tokenStore: new InMemoryTokenStore(),
            httpClient: new HttpClient(new NoCallHandler()));

        var result = await plugin.AuthenticateAsync(context);

        Assert.NotNull(result.AuthUrl);
        var uri = new Uri(result.AuthUrl!);
        var qs = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var state = qs["state"];
        Assert.NotNull(state);
        Assert.True(state!.Length >= 16,
            $"state parameter should be ≥16 chars for CSRF protection, got: '{state}' ({state.Length} chars)");
    }

    [Fact]
    public async Task AuthenticateAsync_AuthUrl_RedirectUri_IsUrlEncoded()
    {
        // redirect_uri must be URL-encoded so the auth server can parse it correctly
        var plugin = new MmfScraperPlugin();
        var context = BuildContext(
            config: OAuthConfig("https://forgekeeper.k8s.inxaos.com/auth/mmf/callback"),
            tokenStore: new InMemoryTokenStore(),
            httpClient: new HttpClient(new NoCallHandler()));

        var result = await plugin.AuthenticateAsync(context);

        Assert.NotNull(result.AuthUrl);
        // The raw URL should have the redirect_uri encoded (/ → %2F, : → %3A, etc.)
        Assert.Contains("redirect_uri=https%3A", result.AuthUrl);
    }

    // ─────────────────────────────────────────────
    // HandleAuthCallbackAsync — implicit flow only
    // ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAuthCallbackAsync_WithAccessToken_SavesToken_ReturnsSuccess()
    {
        var tokenStore = new InMemoryTokenStore();
        var plugin = new MmfScraperPlugin();
        var context = BuildContext(tokenStore: tokenStore);

        var result = await plugin.HandleAuthCallbackAsync(
            context,
            new Dictionary<string, string> { ["access_token"] = "tok123" });

        Assert.True(result.Authenticated);
        Assert.Equal("tok123", await tokenStore.GetTokenAsync("access_token"));
    }

    [Fact]
    public async Task HandleAuthCallbackAsync_WithError_ReturnsFailedWithMessage()
    {
        var plugin = new MmfScraperPlugin();
        var context = BuildContext();

        var result = await plugin.HandleAuthCallbackAsync(
            context,
            new Dictionary<string, string>
            {
                ["error"]             = "access_denied",
                ["error_description"] = "User cancelled the flow",
            });

        Assert.False(result.Authenticated);
        Assert.NotNull(result.Message);
        Assert.Contains("access_denied", result.Message);
    }

    [Fact]
    public async Task HandleAuthCallbackAsync_WithNoParams_ReturnsFailed()
    {
        var plugin = new MmfScraperPlugin();
        var context = BuildContext();

        var result = await plugin.HandleAuthCallbackAsync(
            context,
            new Dictionary<string, string>());

        Assert.False(result.Authenticated);
        Assert.NotNull(result.Message);
        Assert.DoesNotContain("authorization code", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAuthCallbackAsync_WithEmptyAccessToken_ReturnsFailed()
    {
        var plugin = new MmfScraperPlugin();
        var context = BuildContext();

        var result = await plugin.HandleAuthCallbackAsync(
            context,
            new Dictionary<string, string> { ["access_token"] = "" });

        Assert.False(result.Authenticated);
    }

    // ─────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────

    private static PluginContext BuildContext(
        IReadOnlyDictionary<string, string>? config = null,
        ITokenStore? tokenStore = null,
        HttpClient? httpClient = null)
    {
        return new PluginContext
        {
            SourceDirectory = "/tmp/test",
            Config = config ?? new Dictionary<string, string>(),
            HttpClient = httpClient ?? new HttpClient(),
            Logger = NullLogger.Instance,
            TokenStore = tokenStore ?? new InMemoryTokenStore(),
            Progress = new Progress<ScrapeProgress>(),
        };
    }

    /// <summary>Standard OAuth config for tests.</summary>
    private static Dictionary<string, string> OAuthConfig(string? callbackUrl = null) =>
        new()
        {
            ["MMF_USERNAME"]  = "user@example.com",
            ["MMF_PASSWORD"]  = "hunter2",
            ["CLIENT_ID"]     = "downloader_v2",
            ["CLIENT_SECRET"] = "6b511607-740d-49ad-8e31-3bb8b75dd354",
            ["CALLBACK_URL"]  = callbackUrl ?? "https://forgekeeper.k8s.inxaos.com/auth/mmf/callback",
        };

    // ─────────────────────────────────────────────
    // Test HttpMessageHandler stubs
    // ─────────────────────────────────────────────

    /// <summary>Returns a fixed response for requests to a specific URI; throws for others.</summary>
    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly Uri _expectedUri;
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;
        private int _callCount;
        public int CallCount => _callCount;

        public StaticResponseHandler(Uri expectedUri, HttpStatusCode statusCode, string body)
        {
            _expectedUri = expectedUri;
            _statusCode = statusCode;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri != _expectedUri.AbsoluteUri)
                throw new InvalidOperationException($"Unexpected URI: {request.RequestUri} (expected {_expectedUri})");
            Interlocked.Increment(ref _callCount);
            var resp = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }

    /// <summary>Always throws; ensures the HttpClient is never called.</summary>
    private sealed class NoCallHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException($"HttpClient should not have been called, but got: {request.RequestUri}");
    }

    /// <summary>Always throws the given exception (simulates network failure).</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;
        public ThrowingHandler(Exception ex) { _exception = ex; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw _exception;
    }
}
