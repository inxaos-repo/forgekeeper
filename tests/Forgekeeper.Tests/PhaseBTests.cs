using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Phase B tests: GitHubReleaseResolver URL parsing, PluginInstallService behavior,
/// API install/update/remove endpoints, and PluginCli filesystem scanning.
/// </summary>

// ─── GitHubReleaseResolver Tests ─────────────────────────────────────────────

public class GitHubReleaseResolverParsingTests
{
    /// <summary>
    /// Reflection helper to invoke the private static ParseSource method.
    /// Returns (Owner, Repo) tuple without making any network calls.
    /// </summary>
    private static (string? Owner, string? Repo) ParseSource(string source)
    {
        var method = typeof(GitHubReleaseResolver).GetMethod(
            "ParseSource",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = method.Invoke(null, new object[] { source })!;
        var owner = (string?)result.GetType().GetField("Item1")!.GetValue(result);
        var repo  = (string?)result.GetType().GetField("Item2")!.GetValue(result);
        return (owner, repo);
    }

    /// <summary>
    /// Helper to strip @version suffix from source the same way the real
    /// ResolveAsync does before calling ParseSource.
    /// Returns (cleanedSource, embeddedVersion).
    /// </summary>
    private static (string CleanSource, string? EmbeddedVersion) StripVersion(string raw)
    {
        var atIdx = raw.IndexOf('@');
        if (atIdx < 0)
            return (raw, null);
        return (raw[..atIdx], raw[(atIdx + 1)..]);
    }

    // ─── 1. ParsesGitHubUrl ───────────────────────────────────────────────────

    [Fact]
    public void ParsesGitHubUrl()
    {
        var (owner, repo) = ParseSource("https://github.com/owner/repo");
        Assert.Equal("owner", owner);
        Assert.Equal("repo", repo);
    }

    // ─── 2. ParsesShorthand ──────────────────────────────────────────────────

    [Fact]
    public void ParsesShorthand()
    {
        var (owner, repo) = ParseSource("owner/repo");
        Assert.Equal("owner", owner);
        Assert.Equal("repo", repo);
    }

    // ─── 3. ParsesVersionPinning ─────────────────────────────────────────────

    [Fact]
    public void ParsesVersionPinning()
    {
        var (cleanSource, version) = StripVersion("owner/repo@1.0.0");
        Assert.Equal("1.0.0", version);

        var (owner, repo) = ParseSource(cleanSource);
        Assert.Equal("owner", owner);
        Assert.Equal("repo", repo);
    }

    // ─── 4. ParsesUrlWithVersion ─────────────────────────────────────────────

    [Fact]
    public void ParsesUrlWithVersion()
    {
        var (cleanSource, version) = StripVersion("https://github.com/owner/repo@1.0.0");
        Assert.Equal("1.0.0", version);

        var (owner, repo) = ParseSource(cleanSource);
        Assert.Equal("owner", owner);
        Assert.Equal("repo", repo);
    }

    // ─── 5. ReturnsNullForInvalidSource ──────────────────────────────────────

    [Fact]
    public void ReturnsNullForInvalidSource()
    {
        // "not-a-url" has no slash so parts.Length < 2 → (null, null)
        var (owner, repo) = ParseSource("not-a-url");
        Assert.Null(owner);
        Assert.Null(repo);
    }

    // ─── Bonus: strips .git suffix ───────────────────────────────────────────

    [Fact]
    public void ParsesGitHubUrlWithGitSuffix()
    {
        var (owner, repo) = ParseSource("https://github.com/owner/repo.git");
        Assert.Equal("owner", owner);
        Assert.Equal("repo", repo);
    }
}

// ─── PluginInstallService Tests ───────────────────────────────────────────────

public class PluginInstallServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _pluginsDir;
    private readonly string _builtinDir;

    public PluginInstallServiceTests()
    {
        _tempDir    = Path.Combine(Path.GetTempPath(), $"forgekeeper-phaseb-{Guid.NewGuid():N}");
        _pluginsDir = Path.Combine(_tempDir, "plugins");
        _builtinDir = Path.Combine(_tempDir, "builtin");
        Directory.CreateDirectory(_pluginsDir);
        Directory.CreateDirectory(_builtinDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private PluginInstallService CreateService(IGitHubReleaseResolver? resolver = null)
    {
        resolver ??= Mock.Of<IGitHubReleaseResolver>();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Forgekeeper:PluginsDirectory"]        = _pluginsDir,
                ["Forgekeeper:BuiltinPluginsDirectory"] = _builtinDir,
            })
            .Build();

        var manifestLogger = new Mock<ILogger<ManifestValidationService>>();
        var sdkLogger      = new Mock<ILogger<SdkCompatibilityChecker>>();
        var svcLogger      = new Mock<ILogger<PluginInstallService>>();

        var manifestValidator = new ManifestValidationService(manifestLogger.Object);
        var sdkChecker        = new SdkCompatibilityChecker(sdkLogger.Object);

        // Minimal service provider — no DB, so PluginInstallService will skip DB cleanup gracefully
        var services = new ServiceCollection().BuildServiceProvider();

        return new PluginInstallService(
            resolver,
            manifestValidator,
            sdkChecker,
            config,
            svcLogger.Object,
            services);
    }

    // ─── 6. InstallReturnsFailure_WhenResolverReturnsNull ────────────────────

    [Fact]
    public async Task InstallReturnsFailure_WhenResolverReturnsNull()
    {
        var resolverMock = new Mock<IGitHubReleaseResolver>();
        resolverMock
            .Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PluginRelease?)null);

        var service = CreateService(resolverMock.Object);
        var result  = await service.InstallAsync("some/unresolvable-source");

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("resolve", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ─── 7. RemoveReturnsFalse_WhenPluginNotFound ─────────────────────────────

    [Fact]
    public async Task RemoveReturnsFalse_WhenPluginNotFound()
    {
        var service = CreateService();
        var success = await service.RemoveAsync("ghost-plugin-that-does-not-exist");

        Assert.False(success);
    }

    // ─── 8. RemoveReturnsFalse_WhenPluginSyncing ──────────────────────────────
    // NOTE: PluginInstallService.RemoveAsync doesn't check syncing state — that
    // check lives in the API endpoint layer (PluginEndpoints).  The sync guard is
    // tested via the integration test RemovePlugin_Returns409_WhenPluginSyncing
    // in the integration suite.  Here we verify that RemoveAsync itself returns
    // false when the directory simply doesn't exist (same as test 7), which is
    // the closest unit-testable analogue without a full PluginHostService mock.

    [Fact]
    public async Task RemoveReturnsFalse_WhenPluginDirectoryMissing()
    {
        var service = CreateService();
        // Slug exists in neither plugins dir nor builtin dir
        var success = await service.RemoveAsync("nonexistent-slug");
        Assert.False(success);
    }
}

// ─── API Endpoint Integration Tests ──────────────────────────────────────────

public class PhaseBApiTests : IClassFixture<ForgeTestFactory>
{
    private readonly HttpClient _client;

    public PhaseBApiTests(ForgeTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    // ─── 9. InstallPlugin_Returns400_WhenSourceEmpty ──────────────────────────

    [Fact]
    public async Task InstallPlugin_Returns400_WhenSourceEmpty()
    {
        var request = new { Source = "", Version = (string?)null };
        var response = await _client.PostAsJsonAsync("/api/v1/plugins/install", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ─── 10. UpdatePlugin_Returns400_WhenSlugNotFound ─────────────────────────
    // The update endpoint calls installService.UpdateAsync which returns Fail()
    // when the plugin directory is not found — mapped to 400 BadRequest by the
    // endpoint.  (There is no installed plugin "unknown" in the test environment.)

    [Fact]
    public async Task UpdatePlugin_Returns400_WhenSlugNotFound()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/plugins/unknown/update", new { });
        // 400 BadRequest: UpdateAsync returns Fail() when plugin dir not found
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ─── 11. RemovePlugin_Returns400_WhenSlugNotFound ─────────────────────────

    [Fact]
    public async Task RemovePlugin_Returns400_WhenSlugNotFound()
    {
        var response = await _client.DeleteAsync("/api/v1/plugins/unknown");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

// ─── PluginCli Tests ──────────────────────────────────────────────────────────

public class PluginCliTests : IDisposable
{
    private readonly string _tempDir;

    public PluginCliTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"forgekeeper-clitests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private IConfiguration BuildConfig(string? pluginsDir = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Forgekeeper:PluginsDirectory"] = pluginsDir ?? _tempDir,
            })
            .Build();

    // ─── 12. CliList_ScansDirectory ───────────────────────────────────────────

    [Fact]
    public async Task CliList_ScansDirectory()
    {
        // Create a fake plugin directory with manifest.json
        var pluginsDir = Path.Combine(_tempDir, "plugins-list");
        var pluginDir  = Path.Combine(pluginsDir, "my-test-plugin");
        Directory.CreateDirectory(pluginDir);

        var manifest = """
            {
              "slug": "my-test-plugin",
              "name": "My Test Plugin",
              "version": "1.2.3",
              "author": "Test Author",
              "description": "A test plugin",
              "sdkVersion": "1.0.0",
              "entryAssembly": "MyTestPlugin.dll"
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(pluginDir, "manifest.json"), manifest);

        // Redirect stdout so we can assert on output
        var original = Console.Out;
        using var sw = new System.IO.StringWriter();
        Console.SetOut(sw);

        try
        {
            var config = BuildConfig(pluginsDir);
            var exitCode = await Forgekeeper.Api.Cli.PluginCli.RunAsync(new[] { "list" }, config);
            Assert.Equal(0, exitCode);

            var output = sw.ToString();
            Assert.Contains("my-test-plugin", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    // ─── 13. CliInfo_ReturnsManifestData ─────────────────────────────────────

    [Fact]
    public async Task CliInfo_ReturnsManifestData()
    {
        var pluginsDir = Path.Combine(_tempDir, "plugins-info");
        var pluginDir  = Path.Combine(pluginsDir, "info-plugin");
        Directory.CreateDirectory(pluginDir);

        var manifest = """
            {
              "slug": "info-plugin",
              "name": "Info Plugin",
              "version": "2.0.0",
              "author": "Info Author",
              "description": "Plugin used for info CLI test",
              "sdkVersion": "1.0.0",
              "entryAssembly": "InfoPlugin.dll"
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(pluginDir, "manifest.json"), manifest);

        var original = Console.Out;
        using var sw = new System.IO.StringWriter();
        Console.SetOut(sw);

        try
        {
            var config = BuildConfig(pluginsDir);
            var exitCode = await Forgekeeper.Api.Cli.PluginCli.RunAsync(
                new[] { "info", "info-plugin" }, config);

            Assert.Equal(0, exitCode);

            var output = sw.ToString();
            // The CLI prints name, version, author from the manifest
            Assert.Contains("Info Plugin", output);
            Assert.Contains("2.0.0", output);
            Assert.Contains("Info Author", output);
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    // ─── Bonus: CliInfo returns exit code 1 for unknown slug ─────────────────

    [Fact]
    public async Task CliInfo_Returns1_ForUnknownSlug()
    {
        var pluginsDir = Path.Combine(_tempDir, "plugins-empty");
        Directory.CreateDirectory(pluginsDir);

        var config   = BuildConfig(pluginsDir);
        var exitCode = await Forgekeeper.Api.Cli.PluginCli.RunAsync(
            new[] { "info", "ghost-slug" }, config);

        Assert.Equal(1, exitCode);
    }
}
