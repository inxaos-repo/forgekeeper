using System.Text.Json;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Unit tests for MetadataWritebackService.WritebackAsync.
/// Verifies that user-owned fields are correctly written to metadata.json
/// using a temporary directory — no real model paths, no DB needed.
/// </summary>
public class MetadataWritebackTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MetadataWritebackService _service;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public MetadataWritebackTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"forgekeeper_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _service = new MetadataWritebackService(NullLogger<MetadataWritebackService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string MetadataPath => Path.Combine(_tempDir, "metadata.json");

    private async Task WriteInitialMetadata(object content)
    {
        var json = JsonSerializer.Serialize(content, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(MetadataPath, json);
    }

    private async Task<Dictionary<string, JsonElement>> ReadMetadata()
    {
        var json = await File.ReadAllTextAsync(MetadataPath);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, ReadOptions)
               ?? new Dictionary<string, JsonElement>();
    }

    // ─── Tests ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Writeback_WritesRating_ToMetadataJson()
    {
        await WriteInitialMetadata(new { name = "Test Model" });

        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            BasePath = _tempDir,
            Rating = 4,
        };

        await _service.WritebackAsync(model);

        var result = await ReadMetadata();
        Assert.True(result.ContainsKey("userRating"), "Expected 'userRating' key");
        Assert.Equal(4, result["userRating"].GetInt32());
    }

    [Fact]
    public async Task Writeback_WritesNotes_ToMetadataJson()
    {
        await WriteInitialMetadata(new { name = "Test Model" });

        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            BasePath = _tempDir,
            Notes = "Needs supports on the horns",
        };

        await _service.WritebackAsync(model);

        var result = await ReadMetadata();
        Assert.True(result.ContainsKey("notes"), "Expected 'notes' key");
        Assert.Equal("Needs supports on the horns", result["notes"].GetString());
    }

    [Fact]
    public async Task Writeback_WritesCategory_ToMetadataJson()
    {
        await WriteInitialMetadata(new { name = "Test Model" });

        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            BasePath = _tempDir,
            Category = "terrain",
        };

        await _service.WritebackAsync(model);

        var result = await ReadMetadata();
        Assert.True(result.ContainsKey("category"), "Expected 'category' key");
        Assert.Equal("terrain", result["category"].GetString());
    }

    [Fact]
    public async Task Writeback_WritesGameSystem_ToMetadataJson()
    {
        await WriteInitialMetadata(new { name = "Test Model" });

        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            BasePath = _tempDir,
            GameSystem = "Warhammer 40K",
        };

        await _service.WritebackAsync(model);

        var result = await ReadMetadata();
        Assert.True(result.ContainsKey("gameSystem"), "Expected 'gameSystem' key");
        Assert.Equal("Warhammer 40K", result["gameSystem"].GetString());
    }

    [Fact]
    public async Task Writeback_WritesScale_ToMetadataJson()
    {
        await WriteInitialMetadata(new { name = "Test Model" });

        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            BasePath = _tempDir,
            Scale = "28mm",
        };

        await _service.WritebackAsync(model);

        var result = await ReadMetadata();
        Assert.True(result.ContainsKey("scale"), "Expected 'scale' key");
        Assert.Equal("28mm", result["scale"].GetString());
    }

    [Fact]
    public async Task Writeback_WritesPrintStatus_ToMetadataJson()
    {
        await WriteInitialMetadata(new { name = "Test Model" });

        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            BasePath = _tempDir,
            PrintStatus = "printed",
        };

        await _service.WritebackAsync(model);

        var result = await ReadMetadata();
        Assert.True(result.ContainsKey("printStatus"), "Expected 'printStatus' key");
        Assert.Equal("printed", result["printStatus"].GetString());
    }

    [Fact]
    public async Task Writeback_WritesCollectionName_ToMetadataJson()
    {
        await WriteInitialMetadata(new { name = "Test Model" });

        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            BasePath = _tempDir,
            CollectionName = "Grimdark Future",
        };

        await _service.WritebackAsync(model);

        var result = await ReadMetadata();
        Assert.True(result.ContainsKey("collection"), "Expected 'collection' key");
        Assert.Equal("Grimdark Future", result["collection"].GetString());
    }

    [Fact]
    public async Task Writeback_WritesUserTags_ToMetadataJson()
    {
        await WriteInitialMetadata(new { name = "Test Model" });

        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            BasePath = _tempDir,
            Tags = new List<Tag>
            {
                new Tag { Id = Guid.NewGuid(), Name = "painted", Source = "user" },
                new Tag { Id = Guid.NewGuid(), Name = "display-piece", Source = "user" },
            },
        };

        await _service.WritebackAsync(model);

        var result = await ReadMetadata();
        Assert.True(result.ContainsKey("userTags"), "Expected 'userTags' key");
        var tagArray = result["userTags"].EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("painted", tagArray);
        Assert.Contains("display-piece", tagArray);
    }

    [Fact]
    public async Task Writeback_WritesPrintHistory_ToMetadataJson()
    {
        await WriteInitialMetadata(new { name = "Test Model" });

        var printId = Guid.NewGuid();
        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            BasePath = _tempDir,
            PrintHistory = new List<PrintHistoryEntry>
            {
                new PrintHistoryEntry
                {
                    Id = printId,
                    Date = "2026-04-18",
                    Printer = "Bambu Lab A1",
                    Result = "success",
                },
            },
        };

        await _service.WritebackAsync(model);

        var result = await ReadMetadata();
        Assert.True(result.ContainsKey("printHistory"), "Expected 'printHistory' key");
        Assert.Equal(JsonValueKind.Array, result["printHistory"].ValueKind);
        Assert.Equal(1, result["printHistory"].GetArrayLength());
    }

    [Fact]
    public async Task Writeback_PreservesExistingFields()
    {
        // Existing metadata has source-owned fields that should not be clobbered
        await WriteInitialMetadata(new
        {
            name = "Original Name",
            sourceUrl = "https://example.com/model/123",
            downloadedAt = "2026-01-01",
        });

        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            BasePath = _tempDir,
            Rating = 3,
        };

        await _service.WritebackAsync(model);

        var result = await ReadMetadata();
        // Source-owned fields preserved
        Assert.True(result.ContainsKey("name"), "Expected 'name' key preserved");
        Assert.True(result.ContainsKey("sourceUrl"), "Expected 'sourceUrl' key preserved");
        Assert.True(result.ContainsKey("downloadedAt"), "Expected 'downloadedAt' key preserved");
        // User-owned field written
        Assert.Equal(3, result["userRating"].GetInt32());
    }

    [Fact]
    public async Task Writeback_CreatesMetadataFile_IfNoneExists()
    {
        // Don't seed any metadata.json — service should create it from scratch
        Assert.False(File.Exists(MetadataPath));

        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            BasePath = _tempDir,
            Rating = 5,
            Notes = "Created from scratch",
        };

        await _service.WritebackAsync(model);

        Assert.True(File.Exists(MetadataPath), "metadata.json should be created");
        var result = await ReadMetadata();
        Assert.Equal(5, result["userRating"].GetInt32());
        Assert.Equal("Created from scratch", result["notes"].GetString());
    }

    [Fact]
    public async Task Writeback_SetsLastWriteback_Timestamp()
    {
        await WriteInitialMetadata(new { name = "Test Model" });

        var before = DateTime.UtcNow.AddSeconds(-1);

        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            BasePath = _tempDir,
            Rating = 2,
        };

        await _service.WritebackAsync(model);

        var result = await ReadMetadata();
        Assert.True(result.ContainsKey("lastWriteback"), "Expected 'lastWriteback' key");

        var tsStr = result["lastWriteback"].GetString();
        Assert.NotNull(tsStr);
        var ts = DateTime.Parse(tsStr!, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.True(ts >= before, "lastWriteback should be recent");
    }

    [Fact]
    public async Task Writeback_IsNoOp_WhenBasePathIsEmpty()
    {
        // Should not throw — just silently skip
        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            BasePath = string.Empty,
            Rating = 5,
        };

        // No exception expected
        await _service.WritebackAsync(model);
    }
}
