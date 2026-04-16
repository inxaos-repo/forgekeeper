using Xunit;
using Moq;
using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Tests;

/// <summary>
/// Tests for MetadataService — read, write, merge, and backfill operations.
/// Uses real temp directories for filesystem operations.
/// </summary>
public class MetadataServiceTests : IDisposable
{
    private readonly MetadataService _service;
    private readonly string _tempDir;

    public MetadataServiceTests()
    {
        var logger = new Mock<ILogger<MetadataService>>();
        _service = new MetadataService(logger.Object);
        _tempDir = Path.Combine(Path.GetTempPath(), $"forgekeeper-metadata-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task ReadAsync_ReturnsNull_WhenFileDoesNotExist()
    {
        var result = await _service.ReadAsync(Path.Combine(_tempDir, "nonexistent"));
        Assert.Null(result);
    }

    [Fact]
    public async Task ReadAsync_ReturnsNull_WhenFileMalformed()
    {
        var modelDir = Path.Combine(_tempDir, "malformed");
        Directory.CreateDirectory(modelDir);
        await File.WriteAllTextAsync(Path.Combine(modelDir, "metadata.json"), "not json at all");

        var result = await _service.ReadAsync(modelDir);
        Assert.Null(result);
    }

    [Fact]
    public async Task ReadAsync_ParsesValidMetadata()
    {
        var modelDir = Path.Combine(_tempDir, "valid");
        Directory.CreateDirectory(modelDir);
        await File.WriteAllTextAsync(Path.Combine(modelDir, "metadata.json"), """
        {
          "metadataVersion": 1,
          "source": "mmf",
          "externalId": "12345",
          "name": "Space Marine",
          "tags": ["warhammer", "40k"],
          "creator": {
            "displayName": "TestCreator"
          }
        }
        """);

        var result = await _service.ReadAsync(modelDir);

        Assert.NotNull(result);
        Assert.Equal(1, result.MetadataVersion);
        Assert.Equal("mmf", result.Source);
        Assert.Equal("12345", result.ExternalId);
        Assert.Equal("Space Marine", result.Name);
        Assert.Equal(2, result.Tags!.Count);
        Assert.Contains("warhammer", result.Tags);
        Assert.Equal("TestCreator", result.Creator!.DisplayName);
    }

    [Fact]
    public async Task WriteAsync_CreatesMetadataFile()
    {
        var modelDir = Path.Combine(_tempDir, "write-test");
        Directory.CreateDirectory(modelDir);

        var metadata = new SourceMetadata
        {
            MetadataVersion = 1,
            Source = "manual",
            ExternalId = "test-123",
            Name = "Test Model",
            Tags = ["test", "miniature"],
        };

        await _service.WriteAsync(modelDir, metadata);

        var path = Path.Combine(modelDir, "metadata.json");
        Assert.True(File.Exists(path));

        var readBack = await _service.ReadAsync(modelDir);
        Assert.NotNull(readBack);
        Assert.Equal("Test Model", readBack.Name);
        Assert.Equal("manual", readBack.Source);
        Assert.Contains("test", readBack.Tags!);
    }

    [Fact]
    public async Task MergeAsync_UnionsMergeTags()
    {
        var modelDir = Path.Combine(_tempDir, "merge-tags");
        Directory.CreateDirectory(modelDir);

        // Write initial metadata with some tags
        await File.WriteAllTextAsync(Path.Combine(modelDir, "metadata.json"), """
        {
          "metadataVersion": 1,
          "source": "mmf",
          "externalId": "123",
          "name": "Original Name",
          "tags": ["warhammer", "40k"]
        }
        """);

        // Create a model with additional tags
        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            Name = "Original Name",
            Source = SourceType.Mmf,
            Tags =
            [
                new Tag { Id = Guid.NewGuid(), Name = "40k" },     // already exists
                new Tag { Id = Guid.NewGuid(), Name = "infantry" }, // new tag
                new Tag { Id = Guid.NewGuid(), Name = "printed" },  // new tag
            ],
        };

        await _service.MergeAsync(modelDir, model);

        var merged = await _service.ReadAsync(modelDir);
        Assert.NotNull(merged);
        Assert.Contains("warhammer", merged.Tags!);
        Assert.Contains("40k", merged.Tags!);
        Assert.Contains("infantry", merged.Tags!);
        Assert.Contains("printed", merged.Tags!);
        Assert.Equal(4, merged.Tags!.Count); // Union, no duplicates
    }

    [Fact]
    public async Task MergeAsync_PreservesScraperOwnedFields()
    {
        var modelDir = Path.Combine(_tempDir, "merge-preserve");
        Directory.CreateDirectory(modelDir);

        await File.WriteAllTextAsync(Path.Combine(modelDir, "metadata.json"), """
        {
          "metadataVersion": 1,
          "source": "mmf",
          "externalId": "999",
          "name": "Scraper Name",
          "description": "Scraper description - should be preserved"
        }
        """);

        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            Name = "Different Name",
            Source = SourceType.Mmf,
            Tags = [],
        };

        await _service.MergeAsync(modelDir, model);

        var merged = await _service.ReadAsync(modelDir);
        Assert.NotNull(merged);
        // Source/name/description are scraper-owned — the original should be preserved
        Assert.Equal("mmf", merged.Source);
        Assert.Equal("999", merged.ExternalId);
    }

    [Fact]
    public async Task MergeAsync_MergesComponents()
    {
        var modelDir = Path.Combine(_tempDir, "merge-components");
        Directory.CreateDirectory(modelDir);

        await File.WriteAllTextAsync(Path.Combine(modelDir, "metadata.json"), """
        {
          "metadataVersion": 1,
          "source": "mmf",
          "externalId": "456",
          "name": "Multi-Part Model"
        }
        """);

        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            Name = "Multi-Part Model",
            Source = SourceType.Mmf,
            Tags = [],
            Components =
            [
                new ComponentInfo { Name = "Body", File = "body.stl", Required = true },
                new ComponentInfo { Name = "Sword", File = "sword.stl", Required = true, Group = "weapon" },
            ],
        };

        await _service.MergeAsync(modelDir, model);

        var merged = await _service.ReadAsync(modelDir);
        Assert.NotNull(merged);
        Assert.NotNull(merged.Components);
        Assert.Equal(2, merged.Components.Count);
        Assert.Equal("Body", merged.Components[0].Name);
    }

    [Fact]
    public async Task MergeAsync_FallsBackToBackfill_WhenNoFileExists()
    {
        var modelDir = Path.Combine(_tempDir, "merge-backfill");
        Directory.CreateDirectory(modelDir);

        // No metadata.json exists
        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            Name = "Backfilled Model",
            Source = SourceType.Manual,
            CreatorId = Guid.NewGuid(),
            Creator = new Creator { Name = "TestCreator" },
            Tags = [new Tag { Id = Guid.NewGuid(), Name = "test" }],
            CreatedAt = DateTime.UtcNow,
        };

        await _service.MergeAsync(modelDir, model);

        // Should have created a backfill file
        var created = await _service.ReadAsync(modelDir);
        Assert.NotNull(created);
        Assert.Equal("Backfilled Model", created.Name);
        Assert.Equal("manual", created.Source);
    }

    [Fact]
    public async Task BackfillAsync_DoesNotOverwriteExistingFile()
    {
        var modelDir = Path.Combine(_tempDir, "backfill-nooverwrite");
        Directory.CreateDirectory(modelDir);

        // Write an existing metadata.json
        await File.WriteAllTextAsync(Path.Combine(modelDir, "metadata.json"), """
        {
          "metadataVersion": 1,
          "source": "mmf",
          "externalId": "original",
          "name": "Original"
        }
        """);

        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            Name = "New Name",
            Source = SourceType.Manual,
            Creator = new Creator { Name = "Test" },
            Tags = [],
            CreatedAt = DateTime.UtcNow,
        };

        await _service.BackfillAsync(modelDir, model);

        // Should still have original content
        var result = await _service.ReadAsync(modelDir);
        Assert.NotNull(result);
        Assert.Equal("Original", result.Name);
        Assert.Equal("original", result.ExternalId);
    }

    [Fact]
    public async Task BackfillAsync_EnumeratesModelFiles()
    {
        var modelDir = Path.Combine(_tempDir, "backfill-files");
        Directory.CreateDirectory(modelDir);
        Directory.CreateDirectory(Path.Combine(modelDir, "supported"));

        await File.WriteAllTextAsync(Path.Combine(modelDir, "supported", "model.stl"), "stl data");
        await File.WriteAllTextAsync(Path.Combine(modelDir, "readme.txt"), "info");

        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            Name = "File Enum Test",
            Source = SourceType.Manual,
            Creator = new Creator { Name = "Test" },
            Tags = [],
            CreatedAt = DateTime.UtcNow,
        };

        await _service.BackfillAsync(modelDir, model);

        var result = await _service.ReadAsync(modelDir);
        Assert.NotNull(result);
        Assert.NotNull(result.Files);
        Assert.Equal(2, result.Files.Count); // model.stl + readme.txt (metadata.json excluded)
        Assert.Contains(result.Files, f => f.Filename == "model.stl");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
