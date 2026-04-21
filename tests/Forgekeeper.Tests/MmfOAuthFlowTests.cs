using System.Net;
using System.Text;
using Forgekeeper.PluginSdk;
using Forgekeeper.Scraper.Mmf;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Tests for the MMF OAuth 2.0 authorization-code flow (PR: feat/mmf-oauth-authcode-flow).
///
/// Covers:
///   - Config schema: CLIENT_SECRET field exists, is Optional, is Secret type
///   - Legacy implicit-flow callback still works (back-compat)
///   - Auth-code flow: code → token exchange → save access_token + refresh_token + expires_at
///   - Auth-code flow: missing config → graceful failure
///   - Error callback → failure with message
///   - AuthenticateAsync → returns Failed when credentials missing
///   - AuthenticateAsync → returns NeedsBrowser when OAuth configured + no valid token
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
        // CLIENT_SECRET is not Required — file downloads are opt-in.
        // Manifest sync works without it.
        var plugin = new MmfScraperPlugin();
        var field = plugin.ConfigSchema.FirstOrDefault(f => f.Key == "CLIENT_SECRET");

        Assert.NotNull(field);
        Assert.False(field!.Required,
            "CLIENT_SECRET must be optional so manifest-only sync works without registering an OAuth app.");
    }

    [Fact]
    public void ConfigSchema_ClientId_HelpText_MentionsClientSecret()
    {
        // CLIENT_ID help text should reference CLIENT_SECRET to guide users
        var plugin = new MmfScraperPlugin();
        var field = plugin.ConfigSchema.FirstOrDefault(f => f.Key == "CLIENT_ID");

        Assert.NotNull(field);
        Assert.Contains("CLIENT_SECRET", field!.HelpText, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────
    // AuthenticateAsync tests
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
        var plugin = new MmfScraperPlugin();
        var context = BuildContext(config: new Dictionary<string, string>
        {
            ["MMF_USERNAME"] = "user@example.com",
            ["MMF_PASSWORD"] = "hunter2",
        });

        var result = await plugin.AuthenticateAsync(context);

        Assert.True(result.Authenticated);
        Assert.Null(result.AuthUrl); // No OAuth configured → no browser redirect
    }

    [Fact]
    public async Task AuthenticateAsync_WithOAuthConfig_AndNoToken_ReturnsNeedsBrowser()
    {
        var tokenStore = new Mock<ITokenStore>();
        // No stored access_token or expiry
        tokenStore.Setup(t => t.GetTokenAsync("access_token", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        tokenStore.Setup(t => t.GetTokenAsync("token_expires_at", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        tokenStore.Setup(t => t.SaveTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var plugin = new MmfScraperPlugin();
        var context = BuildContext(
            config: new Dictionary<string, string>
            {
                ["MMF_USERNAME"]  = "user@example.com",
                ["MMF_PASSWORD"]  = "hunter2",
                ["CLIENT_ID"]     = "downloader_v2",
                ["CLIENT_SECRET"] = "supersecret",
                ["CALLBACK_URL"]  = "https://forgekeeper.k8s.inxaos.com/auth/mmf/callback",
            },
            tokenStore: tokenStore.Object);

        var result = await plugin.AuthenticateAsync(context);

        Assert.False(result.Authenticated);
        Assert.NotNull(result.AuthUrl);
        // Real MMF OAuth endpoint lives at auth.myminifactory.com/web/authorize (implicit flow).
        // Discovered from MiniDownloader's source 2026-04-21. www.myminifactory.com/oauth/authorize
        // 404s for all clients.
        Assert.Contains("auth.myminifactory.com/web/authorize", result.AuthUrl);
        Assert.Contains("response_type=token", result.AuthUrl);
        Assert.Contains("client_id=", result.AuthUrl);
        Assert.Contains("redirect_uri=", result.AuthUrl);
    }

    [Fact]
    public async Task AuthenticateAsync_WithOAuthConfig_AndValidToken_ReturnsSuccess()
    {
        var futureExpiry = (DateTimeOffset.UtcNow + TimeSpan.FromHours(1)).ToUnixTimeSeconds().ToString();
        var tokenStore = new Mock<ITokenStore>();
        tokenStore.Setup(t => t.GetTokenAsync("access_token", It.IsAny<CancellationToken>()))
            .ReturnsAsync("existing-token-value");
        tokenStore.Setup(t => t.GetTokenAsync("token_expires_at", It.IsAny<CancellationToken>()))
            .ReturnsAsync(futureExpiry);

        var plugin = new MmfScraperPlugin();
        var context = BuildContext(
            config: new Dictionary<string, string>
            {
                ["MMF_USERNAME"]  = "user@example.com",
                ["MMF_PASSWORD"]  = "hunter2",
                ["CLIENT_ID"]     = "downloader_v2",
                ["CLIENT_SECRET"] = "supersecret",
                ["CALLBACK_URL"]  = "https://forgekeeper.k8s.inxaos.com/auth/mmf/callback",
            },
            tokenStore: tokenStore.Object);

        var result = await plugin.AuthenticateAsync(context);

        Assert.True(result.Authenticated);
        Assert.Null(result.AuthUrl); // Already have a valid token — no browser needed
    }

    // ─────────────────────────────────────────────
    // HandleAuthCallbackAsync tests
    // ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAuthCallbackAsync_WithAccessToken_LegacyFlow_SavesToken()
    {
        var tokenStore = new Mock<ITokenStore>();
        tokenStore.Setup(t => t.SaveTokenAsync("access_token", "tok123", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        var plugin = new MmfScraperPlugin();
        var context = BuildContext(tokenStore: tokenStore.Object);

        var result = await plugin.HandleAuthCallbackAsync(
            context,
            new Dictionary<string, string> { ["access_token"] = "tok123" });

        Assert.True(result.Authenticated);
        tokenStore.Verify(t => t.SaveTokenAsync("access_token", "tok123", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAuthCallbackAsync_WithCode_ButMissingClientSecret_ReturnsFailed()
    {
        var plugin = new MmfScraperPlugin();
        var context = BuildContext(config: new Dictionary<string, string>
        {
            ["CLIENT_ID"]    = "downloader_v2",
            // CLIENT_SECRET intentionally omitted
            ["CALLBACK_URL"] = "https://forgekeeper.k8s.inxaos.com/auth/mmf/callback",
        });

        var result = await plugin.HandleAuthCallbackAsync(
            context,
            new Dictionary<string, string> { ["code"] = "abc123" });

        Assert.False(result.Authenticated);
        Assert.NotNull(result.Message);
        Assert.Contains("CLIENT_SECRET", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAuthCallbackAsync_WithCode_Success_SavesAllTokens()
    {
        // Use the testable subclass that overrides ExchangeCodeForTokenAsync
        // to avoid real HTTP calls in unit tests.
        var tokenStore = new Mock<ITokenStore>();
        var savedTokens = new Dictionary<string, string>();
        tokenStore.Setup(t => t.SaveTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((k, v, _) => savedTokens[k] = v)
            .Returns(Task.CompletedTask);

        var plugin = new FakeMmfScraperPlugin();
        var context = BuildContext(
            config: new Dictionary<string, string>
            {
                ["CLIENT_ID"]     = "downloader_v2",
                ["CLIENT_SECRET"] = "supersecret",
                ["CALLBACK_URL"]  = "https://forgekeeper.k8s.inxaos.com/auth/mmf/callback",
            },
            tokenStore: tokenStore.Object);

        var result = await plugin.HandleAuthCallbackAsync(
            context,
            new Dictionary<string, string> { ["code"] = "authcode_xyz" });

        Assert.True(result.Authenticated);
        Assert.Contains("access_token", savedTokens.Keys);
        Assert.Contains("refresh_token", savedTokens.Keys);
        Assert.Contains("token_expires_at", savedTokens.Keys);
        Assert.Equal("fake_access_token", savedTokens["access_token"]);
        Assert.Equal("fake_refresh_token", savedTokens["refresh_token"]);
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
    }

    // ─────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────

    private static PluginContext BuildContext(
        IReadOnlyDictionary<string, string>? config = null,
        ITokenStore? tokenStore = null)
    {
        var ts = tokenStore ?? Mock.Of<ITokenStore>();
        return new PluginContext
        {
            SourceDirectory = "/tmp/test",
            Config = config ?? new Dictionary<string, string>(),
            HttpClient = new HttpClient(),
            Logger = NullLogger.Instance,
            TokenStore = ts,
            Progress = new Progress<ScrapeProgress>(),
        };
    }

    /// <summary>
    /// Test double for MmfScraperPlugin that overrides ExchangeCodeForTokenAsync
    /// to avoid real HTTP calls. Returns a predictable fake token response.
    /// </summary>
    private sealed class FakeMmfScraperPlugin : MmfScraperPlugin
    {
        protected override async Task<AuthResult> ExchangeCodeForTokenAsync(
            PluginContext context, string code, string clientId, string clientSecret, string callbackUrl,
            CancellationToken ct)
        {
            // Simulate a successful token exchange without hitting the network
            await context.TokenStore.SaveTokenAsync("access_token", "fake_access_token", ct);
            await context.TokenStore.SaveTokenAsync("refresh_token", "fake_refresh_token", ct);
            await context.TokenStore.SaveTokenAsync(
                "token_expires_at",
                (DateTimeOffset.UtcNow + TimeSpan.FromHours(1)).ToUnixTimeSeconds().ToString(),
                ct);
            return AuthResult.Success("Connected to MyMiniFactory via OAuth");
        }
    }
}
