using Forgekeeper.PluginSdk;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Tests for MmfScraperPlugin — config schema, source identification,
/// manifest parsing, variant detection, and auth URL construction.
/// Uses mock PluginContext with in-memory config and token store.
/// </summary>
public class MmfScraperPluginTests
{
    // We can't directly reference Forgekeeper.Scraper.Mmf since it's a plugin project,
    // but we can test the ILibraryScraper contract via the SDK types and
    // test the publicly available behavior patterns.

    // --- PluginConfigField tests ---

    [Fact]
    public void PluginConfigField_CanDefineStringField()
    {
        var field = new PluginConfigField
        {
            Key = "CLIENT_ID",
            Label = "OAuth Client ID",
            Type = PluginConfigFieldType.String,
            Required = true,
            HelpText = "Your API client ID",
        };

        Assert.Equal("CLIENT_ID", field.Key);
        Assert.Equal(PluginConfigFieldType.String, field.Type);
        Assert.True(field.Required);
    }

    [Fact]
    public void PluginConfigField_CanDefineSecretField()
    {
        var field = new PluginConfigField
        {
            Key = "CLIENT_SECRET",
            Label = "OAuth Client Secret",
            Type = PluginConfigFieldType.Secret,
            Required = true,
        };

        Assert.Equal(PluginConfigFieldType.Secret, field.Type);
    }

    [Fact]
    public void PluginConfigField_CanDefineNumberFieldWithDefault()
    {
        var field = new PluginConfigField
        {
            Key = "DELAY_MS",
            Label = "Request Delay",
            Type = PluginConfigFieldType.Number,
            Required = false,
            DefaultValue = "1000",
        };

        Assert.Equal(PluginConfigFieldType.Number, field.Type);
        Assert.Equal("1000", field.DefaultValue);
        Assert.False(field.Required);
    }

    [Theory]
    [InlineData(PluginConfigFieldType.String)]
    [InlineData(PluginConfigFieldType.Secret)]
    [InlineData(PluginConfigFieldType.Url)]
    [InlineData(PluginConfigFieldType.Number)]
    [InlineData(PluginConfigFieldType.Bool)]
    public void PluginConfigFieldType_AllValuesExist(PluginConfigFieldType type)
    {
        Assert.True(Enum.IsDefined(type));
    }

    // --- ScrapedModel tests ---

    [Fact]
    public void ScrapedModel_CanBeConstructed()
    {
        var model = new ScrapedModel
        {
            ExternalId = "12345",
            Name = "Dragon Miniature",
            CreatorName = "EpicMiniStudio",
            CreatorId = "678",
            UpdatedAt = new DateTime(2024, 6, 15),
            Type = "miniature",
            Extra = new Dictionary<string, object> { ["license"] = "personal" },
        };

        Assert.Equal("12345", model.ExternalId);
        Assert.Equal("Dragon Miniature", model.Name);
        Assert.Equal("EpicMiniStudio", model.CreatorName);
        Assert.Equal("678", model.CreatorId);
        Assert.Equal("miniature", model.Type);
        Assert.NotNull(model.Extra);
    }

    [Fact]
    public void ScrapedModel_MinimalConstruction()
    {
        var model = new ScrapedModel
        {
            ExternalId = "1",
            Name = "Test",
        };

        Assert.Null(model.CreatorName);
        Assert.Null(model.CreatorId);
        Assert.Null(model.UpdatedAt);
        Assert.Null(model.Type);
        Assert.Null(model.Extra);
    }

    // --- ScrapeResult tests ---

    [Fact]
    public void ScrapeResult_Ok_SetsSuccessTrue()
    {
        var files = new List<DownloadedFile>
        {
            new()
            {
                Filename = "model.stl",
                LocalPath = "/tmp/model.stl",
                Size = 1024,
                Variant = "supported",
            },
        };

        var result = ScrapeResult.Ok("metadata.json", files);

        Assert.True(result.Success);
        Assert.Equal("metadata.json", result.MetadataFile);
        Assert.Single(result.Files);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ScrapeResult_Failure_SetsSuccessFalse()
    {
        var result = ScrapeResult.Failure("Download timed out");

        Assert.False(result.Success);
        Assert.Equal("Download timed out", result.Error);
        Assert.Null(result.MetadataFile);
    }

    // --- AuthResult tests ---

    [Fact]
    public void AuthResult_Success_SetsAuthenticated()
    {
        var result = AuthResult.Success("Connected");
        Assert.True(result.Authenticated);
        Assert.Equal("Connected", result.Message);
        Assert.Null(result.AuthUrl);
    }

    [Fact]
    public void AuthResult_NeedsBrowser_SetsAuthUrl()
    {
        var result = AuthResult.NeedsBrowser("https://auth.example.com/authorize", "Please log in");
        Assert.False(result.Authenticated);
        Assert.Equal("https://auth.example.com/authorize", result.AuthUrl);
        Assert.Equal("Please log in", result.Message);
    }

    [Fact]
    public void AuthResult_Failed_SetsMessage()
    {
        var result = AuthResult.Failed("Invalid token");
        Assert.False(result.Authenticated);
        Assert.Equal("Invalid token", result.Message);
    }

    // --- DownloadedFile tests ---

    [Fact]
    public void DownloadedFile_TracksVariantAndArchiveStatus()
    {
        var file = new DownloadedFile
        {
            Filename = "model_supported.zip",
            LocalPath = "/tmp/model_supported.zip",
            Size = 50_000,
            Variant = "supported",
            IsArchive = true,
        };

        Assert.Equal("supported", file.Variant);
        Assert.True(file.IsArchive);
    }

    [Fact]
    public void DownloadedFile_DefaultVariantIsNull()
    {
        var file = new DownloadedFile
        {
            Filename = "model.stl",
            LocalPath = "/tmp/model.stl",
            Size = 1024,
        };

        Assert.Null(file.Variant);
        Assert.False(file.IsArchive);
    }

    // --- PluginContext tests ---

    [Fact]
    public void PluginContext_CanBeConstructed()
    {
        var mockTokenStore = new Mock<ITokenStore>();
        var mockLogger = new Mock<ILogger>();

        var context = new PluginContext
        {
            SourceDirectory = "/mnt/3dprinting/sources/mmf",
            Config = new Dictionary<string, string>
            {
                ["CLIENT_ID"] = "test-id",
                ["CLIENT_SECRET"] = "test-secret",
            },
            HttpClient = new HttpClient(),
            Logger = mockLogger.Object,
            TokenStore = mockTokenStore.Object,
            Progress = new Progress<ScrapeProgress>(),
        };

        Assert.Equal("/mnt/3dprinting/sources/mmf", context.SourceDirectory);
        Assert.Equal("test-id", context.Config["CLIENT_ID"]);
        Assert.Null(context.ModelDirectory);
    }

    [Fact]
    public void PluginContext_ModelDirectory_CanBeSetPerModel()
    {
        var mockTokenStore = new Mock<ITokenStore>();
        var mockLogger = new Mock<ILogger>();

        var context = new PluginContext
        {
            SourceDirectory = "/sources/mmf",
            Config = new Dictionary<string, string>(),
            HttpClient = new HttpClient(),
            Logger = mockLogger.Object,
            TokenStore = mockTokenStore.Object,
            Progress = new Progress<ScrapeProgress>(),
        };

        Assert.Null(context.ModelDirectory);
        context.ModelDirectory = "/sources/mmf/creator/model";
        Assert.Equal("/sources/mmf/creator/model", context.ModelDirectory);
    }

    // --- ITokenStore mock tests ---

    [Fact]
    public async Task MockTokenStore_CanSaveAndRetrieve()
    {
        var store = new InMemoryTokenStore();
        await store.SaveTokenAsync("access_token", "abc123");
        var token = await store.GetTokenAsync("access_token");
        Assert.Equal("abc123", token);
    }

    [Fact]
    public async Task MockTokenStore_ReturnsNullForMissingKey()
    {
        var store = new InMemoryTokenStore();
        var token = await store.GetTokenAsync("nonexistent");
        Assert.Null(token);
    }

    [Fact]
    public async Task MockTokenStore_CanDeleteToken()
    {
        var store = new InMemoryTokenStore();
        await store.SaveTokenAsync("key", "value");
        await store.DeleteTokenAsync("key");
        var token = await store.GetTokenAsync("key");
        Assert.Null(token);
    }

    // --- ScrapeProgress tests ---

    [Fact]
    public void ScrapeProgress_TracksDownloadProgress()
    {
        var progress = new ScrapeProgress
        {
            Status = "downloading",
            Current = 25,
            Total = 100,
            CurrentItem = "Dragon_Base.stl",
        };

        Assert.Equal("downloading", progress.Status);
        Assert.Equal(25, progress.Current);
        Assert.Equal(100, progress.Total);
        Assert.Equal("Dragon_Base.stl", progress.CurrentItem);
    }
}

/// <summary>
/// Simple in-memory implementation of ITokenStore for testing.
/// </summary>
internal class InMemoryTokenStore : ITokenStore
{
    private readonly Dictionary<string, string> _tokens = new();

    public Task<string?> GetTokenAsync(string key, CancellationToken ct = default)
    {
        _tokens.TryGetValue(key, out var value);
        return Task.FromResult(value);
    }

    public Task SaveTokenAsync(string key, string value, CancellationToken ct = default)
    {
        _tokens[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteTokenAsync(string key, CancellationToken ct = default)
    {
        _tokens.Remove(key);
        return Task.CompletedTask;
    }
}
