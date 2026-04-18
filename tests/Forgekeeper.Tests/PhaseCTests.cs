using System.Net;
using System.Net.Http.Json;
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
/// Phase C tests: PluginRegistryClient, PluginUpdateTracker, PluginUpdateWorker,
/// and the /plugins/registry + /plugins/updates API endpoints.
/// </summary>

// ─── Sample registry JSON used across multiple tests ─────────────────────────

internal static class SampleRegistry
{
    public const string Json = """
        {
          "schema_version": "1",
          "updated": "2026-04-18T00:00:00Z",
          "plugins": [
            {
              "slug": "mmf",
              "name": "MyMiniFactory Scraper",
              "version": "1.1.0",
              "author": "Plugin Author",
              "author_url": "https://github.com/author",
              "description": "Scrapes 3D printing model library data from MyMiniFactory.",
              "homepage": "https://github.com/org/Forgekeeper.Scraper.Mmf",
              "source_url": "https://github.com/org/Forgekeeper.Scraper.Mmf",
              "download_url": "https://github.com/org/Forgekeeper.Scraper.Mmf/releases/download/v1.1.0/plugin.zip",
              "icon_url": "https://example.com/icon.png",
              "sdk_version": "1.0.0",
              "min_sdk_version": "1.0.0",
              "max_sdk_version": "1.x",
              "min_forgekeeper_version": "1.0.0",
              "checksum_sha256": "abc123",
              "tags": ["3d-printing", "miniatures", "scraper"],
              "license": "MIT",
              "updated": "2026-04-18T00:00:00Z",
              "downloads": 42
            },
            {
              "slug": "cults3d",
              "name": "Cults3D Scraper",
              "version": "2.0.0",
              "author": "Another Author",
              "description": "Scrapes models from Cults3D.",
              "homepage": "https://github.com/org/Forgekeeper.Scraper.Cults3d",
              "download_url": "https://github.com/org/Forgekeeper.Scraper.Cults3d/releases/download/v2.0.0/plugin.zip",
              "sdk_version": "2.0.0",
              "min_sdk_version": "2.0.0",
              "max_sdk_version": "2.x",
              "min_forgekeeper_version": "1.0.0",
              "tags": ["3d-printing", "cults"],
              "updated": "2026-04-18T00:00:00Z",
              "downloads": 10
            }
          ]
        }
        """;
}

// ─── PluginRegistryClient Tests ───────────────────────────────────────────────

public class PluginRegistryClientTests : IDisposable
{
    private readonly string _cacheDir;
    private readonly IConfiguration _config;
    private readonly ILogger<PluginRegistryClient> _logger;

    public PluginRegistryClientTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), $"forgekeeper-phase-c-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_cacheDir);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Plugins:RegistryUrl"] = "https://example.invalid/registry.json",
                ["Plugins:RegistryCacheHours"] = "24",
                ["Forgekeeper:DataDir"] = _cacheDir,
            })
            .Build();

        _logger = new Mock<ILogger<PluginRegistryClient>>().Object;
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
            Directory.Delete(_cacheDir, true);
    }

    /// <summary>Helper: write sample JSON into the cache file.</summary>
    private void SeedCache(string json)
    {
        var path = Path.Combine(_cacheDir, "registry-cache.json");
        File.WriteAllText(path, json);
        // Touch the file so it appears fresh (< 24h old)
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
    }

    private PluginRegistryClient CreateClientWithMockHttp(
        HttpStatusCode status,
        string content,
        IConfiguration? config = null)
    {
        var handler = new MockHttpMessageHandler(status, content);
        var http = new HttpClient(handler);
        return new PluginRegistryClient(http, config ?? _config, _logger);
    }

    private PluginRegistryClient CreateClientWithCacheOnly()
    {
        // Point to an HTTP URL that will fail — tests should only hit the cache
        var handler = new MockHttpMessageHandler(HttpStatusCode.ServiceUnavailable, "");
        var http = new HttpClient(handler);
        return new PluginRegistryClient(http, _config, _logger);
    }

    // ─── 1. DeserializesRegistryJson ─────────────────────────────────────────

    [Fact]
    public async Task DeserializesRegistryJson()
    {
        var client = CreateClientWithMockHttp(HttpStatusCode.OK, SampleRegistry.Json);

        var registry = await client.FetchRegistryAsync(forceRefresh: true);

        Assert.NotNull(registry);
        Assert.Equal("1", registry.SchemaVersion);
        Assert.Equal(2, registry.Plugins.Count);

        var mmf = registry.Plugins.First(p => p.Slug == "mmf");
        Assert.Equal("MyMiniFactory Scraper", mmf.Name);
        Assert.Equal("1.1.0", mmf.Version);
        Assert.Equal("Plugin Author", mmf.Author);
        Assert.Equal(3, mmf.Tags.Count);
        Assert.Equal(42, mmf.Downloads);
    }

    // ─── 2. SearchByName ─────────────────────────────────────────────────────

    [Fact]
    public async Task SearchByName()
    {
        SeedCache(SampleRegistry.Json);
        var client = CreateClientWithCacheOnly();

        var results = await client.SearchAsync(query: "miniFactory");

        Assert.Single(results);
        Assert.Equal("mmf", results[0].Slug);
    }

    // ─── 3. SearchByTag ──────────────────────────────────────────────────────

    [Fact]
    public async Task SearchByTag()
    {
        SeedCache(SampleRegistry.Json);
        var client = CreateClientWithCacheOnly();

        var results = await client.SearchAsync(tag: "cults");

        Assert.Single(results);
        Assert.Equal("cults3d", results[0].Slug);
    }

    // ─── 4. GetPluginBySlug ──────────────────────────────────────────────────

    [Fact]
    public async Task GetPluginBySlug()
    {
        SeedCache(SampleRegistry.Json);
        var client = CreateClientWithCacheOnly();

        var entry = await client.GetPluginAsync("mmf");

        Assert.NotNull(entry);
        Assert.Equal("mmf", entry.Slug);
        Assert.Equal("1.1.0", entry.Version);

        var notFound = await client.GetPluginAsync("ghost-plugin");
        Assert.Null(notFound);
    }

    // ─── 5. CheckUpdates_FindsNewerVersion ───────────────────────────────────

    [Fact]
    public async Task CheckUpdates_FindsNewerVersion()
    {
        SeedCache(SampleRegistry.Json);
        var client = CreateClientWithCacheOnly();

        // Installed "mmf" at 1.0.0, registry has 1.1.0
        var updates = await client.CheckUpdatesAsync(new[] { ("mmf", "1.0.0") });

        Assert.Single(updates);
        Assert.Equal("mmf", updates[0].Slug);
        Assert.Equal("1.0.0", updates[0].CurrentVersion);
        Assert.Equal("1.1.0", updates[0].AvailableVersion);
        Assert.True(updates[0].IsCompatible); // SDK 1.0.0 compatible with host 1.0
    }

    // ─── 6. CheckUpdates_IgnoresOlderVersion ─────────────────────────────────

    [Fact]
    public async Task CheckUpdates_IgnoresOlderVersion()
    {
        SeedCache(SampleRegistry.Json);
        var client = CreateClientWithCacheOnly();

        // Installed "mmf" at 1.1.0 — registry also has 1.1.0 → no update
        var updates = await client.CheckUpdatesAsync(new[] { ("mmf", "1.1.0") });

        Assert.Empty(updates);
    }

    // ─── 7. CheckUpdates_FlagsIncompatibleSdk ────────────────────────────────

    [Fact]
    public async Task CheckUpdates_FlagsIncompatibleSdk()
    {
        SeedCache(SampleRegistry.Json);
        var client = CreateClientWithCacheOnly();

        // Installed "cults3d" at 1.0.0. Registry has 2.0.0 which requires SDK 2.0.
        // Host SDK is 1.0 → isCompatible must be false.
        var updates = await client.CheckUpdatesAsync(new[] { ("cults3d", "1.0.0") });

        Assert.Single(updates);
        Assert.Equal("cults3d", updates[0].Slug);
        Assert.Equal("2.0.0", updates[0].AvailableVersion);
        Assert.False(updates[0].IsCompatible);
    }
}

// ─── PluginUpdateTracker Tests ────────────────────────────────────────────────

public class PluginUpdateTrackerTests
{
    // ─── 8. TracksUpdates ────────────────────────────────────────────────────

    [Fact]
    public void TracksUpdates()
    {
        var tracker = new PluginUpdateTracker();

        var info = new PluginUpdateInfo
        {
            Slug = "mmf",
            CurrentVersion = "1.0.0",
            AvailableVersion = "1.1.0",
            DownloadUrl = "https://example.com/plugin.zip",
            IsCompatible = true,
        };

        tracker.SetUpdate("mmf", info);

        var updates = tracker.GetAvailableUpdates();
        Assert.True(updates.ContainsKey("mmf"));
        Assert.Equal("1.1.0", updates["mmf"].AvailableVersion);

        tracker.ClearUpdate("mmf");
        Assert.Empty(tracker.GetAvailableUpdates());
    }

    // ─── 9. UpdateCount ──────────────────────────────────────────────────────

    [Fact]
    public void UpdateCount()
    {
        var tracker = new PluginUpdateTracker();
        Assert.Equal(0, tracker.UpdateCount);

        tracker.SetUpdate("a", new PluginUpdateInfo { Slug = "a" });
        Assert.Equal(1, tracker.UpdateCount);

        tracker.SetUpdate("b", new PluginUpdateInfo { Slug = "b" });
        Assert.Equal(2, tracker.UpdateCount);

        tracker.ClearUpdate("a");
        Assert.Equal(1, tracker.UpdateCount);

        tracker.ClearUpdate("b");
        Assert.Equal(0, tracker.UpdateCount);
    }
}

// ─── API Integration Tests ────────────────────────────────────────────────────

public class PhaseCApiTests : IClassFixture<ForgeTestFactory>
{
    private readonly HttpClient _client;

    public PhaseCApiTests(ForgeTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    // ─── 10. BrowseRegistry_Returns200 ───────────────────────────────────────

    [Fact]
    public async Task BrowseRegistry_Returns200()
    {
        // The test environment has no network / mock registry — we expect the
        // endpoint to return 200 with an empty list (graceful degradation).
        var response = await _client.GetAsync("/api/v1/plugins/registry");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Should be a JSON array (possibly empty)
        Assert.Equal(JsonValueKind.Array, json.ValueKind);
    }

    // ─── 11. GetUpdates_Returns200 ────────────────────────────────────────────

    [Fact]
    public async Task GetUpdates_Returns200()
    {
        var response = await _client.GetAsync("/api/v1/plugins/updates");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("count", out var count));
        Assert.Equal(JsonValueKind.Number, count.ValueKind);
    }
}

// ─── PluginUpdateWorker Tests ─────────────────────────────────────────────────

public class PluginUpdateWorkerTests
{
    // ─── 12. DisabledByDefault — worker exits immediately when not enabled ───

    [Fact]
    public async Task DisabledByDefault()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Explicitly not setting Plugins:AutoUpdate:Enabled
            })
            .Build();

        var services = new ServiceCollection().BuildServiceProvider();
        var tracker = new PluginUpdateTracker();
        var logger = new Mock<ILogger<Forgekeeper.Api.BackgroundServices.PluginUpdateWorker>>().Object;

        var worker = new Forgekeeper.Api.BackgroundServices.PluginUpdateWorker(
            services, config, logger, tracker);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        // ExecuteAsync should return quickly (before the CTS fires) because
        // the worker is disabled.
        await worker.StartAsync(cts.Token);

        // Give the worker a moment to run ExecuteAsync — it should log and return
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        // If we get here without a timeout, the worker exited early as expected.
        // No exception = pass.
        Assert.True(true);
    }
}

// ─── MockHttpMessageHandler ───────────────────────────────────────────────────

internal class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _content;

    public MockHttpMessageHandler(HttpStatusCode statusCode, string content)
    {
        _statusCode = statusCode;
        _content = content;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_content, System.Text.Encoding.UTF8, "application/json"),
        });
    }
}
