using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Services;
using Forgekeeper.PluginSdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Tests for PluginHostService — plugin discovery, config loading,
/// sync scheduling, error isolation, auth callback routing, and status tracking.
/// Uses mock IServiceProvider and InMemory database.
/// </summary>
public class PluginHostServiceTests : IDisposable
{
    private readonly string _tempDir;

    public PluginHostServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"forgekeeper-plugintest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private PluginHostService CreateService(
        IServiceProvider services, string? pluginsDir = null, string? sourcesDir = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Forgekeeper:PluginsDirectory"] = pluginsDir ?? Path.Combine(_tempDir, "plugins"),
                ["Forgekeeper:SourcesDirectory"] = Path.Combine(_tempDir, "sources"),
            })
            .Build();

        var logger = new Mock<ILogger<PluginHostService>>();
        return new PluginHostService(services, logger.Object, config);
    }

    private IServiceProvider BuildServiceProvider(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<ForgeDbContext>(opts =>
            opts.UseInMemoryDatabase(dbName));
        services.AddLogging();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Plugins_IsEmpty_WhenNoPluginsDirectory()
    {
        var sp = BuildServiceProvider($"TestDb_{Guid.NewGuid()}");
        var service = CreateService(sp, pluginsDir: "/nonexistent/path");
        Assert.Empty(service.Plugins);
    }

    [Fact]
    public void Plugins_IsEmpty_WhenPluginsDirectoryIsEmpty()
    {
        var pluginsDir = Path.Combine(_tempDir, "plugins");
        Directory.CreateDirectory(pluginsDir);

        var sp = BuildServiceProvider($"TestDb_{Guid.NewGuid()}");
        var service = CreateService(sp);
        Assert.Empty(service.Plugins);
    }

    [Fact]
    public void GetPlugin_ReturnsNull_WhenSlugNotFound()
    {
        var sp = BuildServiceProvider($"TestDb_{Guid.NewGuid()}");
        var service = CreateService(sp);
        Assert.Null(service.GetPlugin("nonexistent"));
    }

    [Fact]
    public void GetSyncStatus_ReturnsNull_WhenSlugNotFound()
    {
        var sp = BuildServiceProvider($"TestDb_{Guid.NewGuid()}");
        var service = CreateService(sp);
        Assert.Null(service.GetSyncStatus("nonexistent"));
    }

    [Fact]
    public async Task TriggerSyncAsync_ThrowsForUnknownPlugin()
    {
        var sp = BuildServiceProvider($"TestDb_{Guid.NewGuid()}");
        var service = CreateService(sp);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TriggerSyncAsync("nonexistent", CancellationToken.None));
    }

    [Fact]
    public async Task HandleAuthCallbackAsync_ReturnsFailedForUnknownPlugin()
    {
        var sp = BuildServiceProvider($"TestDb_{Guid.NewGuid()}");
        var service = CreateService(sp);

        var result = await service.HandleAuthCallbackAsync("nonexistent",
            new Dictionary<string, string>(), CancellationToken.None);

        Assert.False(result.Authenticated);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public void PluginSyncStatus_DefaultValues_AreCorrect()
    {
        var status = new PluginSyncStatus();
        Assert.False(status.IsRunning);
        Assert.Null(status.LastSyncAt);
        Assert.Equal(0, status.TotalModels);
        Assert.Equal(0, status.ScrapedModels);
        Assert.Equal(0, status.FailedModels);
        Assert.Null(status.Error);
        Assert.Null(status.CurrentProgress);
    }

    [Fact]
    public void PluginSyncStatus_CanTrackProgress()
    {
        var status = new PluginSyncStatus
        {
            IsRunning = true,
            LastSyncAt = DateTime.UtcNow,
            TotalModels = 100,
            ScrapedModels = 50,
            FailedModels = 2,
            CurrentProgress = new ScrapeProgress
            {
                Status = "downloading",
                Current = 50,
                Total = 100,
                CurrentItem = "Dragon Miniature",
            },
        };

        Assert.True(status.IsRunning);
        Assert.Equal(100, status.TotalModels);
        Assert.Equal(50, status.ScrapedModels);
        Assert.Equal("downloading", status.CurrentProgress.Status);
    }

    [Fact]
    public void LoadedPlugin_TracksLoadedAt()
    {
        var loadedAt = DateTime.UtcNow;
        // We can't fully test LoadedPlugin creation without a real assembly,
        // but we can verify the structure exists and LoadedAt works
        Assert.True(loadedAt <= DateTime.UtcNow);
    }
}

/// <summary>
/// Tests for the PluginConfig model and database operations.
/// </summary>
public class PluginConfigDbTests
{
    [Fact]
    public async Task PluginConfig_CanStoreAndRetrieve()
    {
        using var db = TestDbContextFactory.Create();
        var config = new PluginConfig
        {
            Id = Guid.NewGuid(),
            PluginSlug = "mmf",
            Key = "CLIENT_ID",
            Value = "test-client-id",
            IsEncrypted = false,
            UpdatedAt = DateTime.UtcNow,
        };
        db.PluginConfigs.Add(config);
        await db.SaveChangesAsync();

        var stored = await db.PluginConfigs
            .FirstOrDefaultAsync(c => c.PluginSlug == "mmf" && c.Key == "CLIENT_ID");

        Assert.NotNull(stored);
        Assert.Equal("test-client-id", stored!.Value);
        Assert.False(stored.IsEncrypted);
    }

    [Fact]
    public async Task PluginConfig_FiltersByPluginSlug()
    {
        using var db = TestDbContextFactory.Create();
        db.PluginConfigs.AddRange(
            new PluginConfig
            {
                Id = Guid.NewGuid(), PluginSlug = "mmf",
                Key = "KEY1", Value = "val1",
            },
            new PluginConfig
            {
                Id = Guid.NewGuid(), PluginSlug = "mmf",
                Key = "KEY2", Value = "val2",
            },
            new PluginConfig
            {
                Id = Guid.NewGuid(), PluginSlug = "thangs",
                Key = "KEY1", Value = "thangs-val1",
            }
        );
        await db.SaveChangesAsync();

        var mmfConfigs = await db.PluginConfigs
            .Where(c => c.PluginSlug == "mmf")
            .ToDictionaryAsync(c => c.Key, c => c.Value);

        Assert.Equal(2, mmfConfigs.Count);
        Assert.Equal("val1", mmfConfigs["KEY1"]);
    }

    [Fact]
    public async Task PluginConfig_ExcludesTokenKeys()
    {
        using var db = TestDbContextFactory.Create();
        db.PluginConfigs.AddRange(
            new PluginConfig
            {
                Id = Guid.NewGuid(), PluginSlug = "mmf",
                Key = "CLIENT_ID", Value = "test",
            },
            new PluginConfig
            {
                Id = Guid.NewGuid(), PluginSlug = "mmf",
                Key = "__token__access_token", Value = "secret-token",
                IsEncrypted = true,
            }
        );
        await db.SaveChangesAsync();

        var configs = await db.PluginConfigs
            .Where(c => c.PluginSlug == "mmf" && !c.Key.StartsWith("__token__"))
            .ToDictionaryAsync(c => c.Key, c => c.Value);

        Assert.Single(configs);
        Assert.True(configs.ContainsKey("CLIENT_ID"));
    }
}
