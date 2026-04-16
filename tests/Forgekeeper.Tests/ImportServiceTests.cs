using Xunit;
using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Services;
using Forgekeeper.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Forgekeeper.Tests;

/// <summary>
/// Tests for ImportService confidence scoring and analysis logic.
/// Uses a real InMemory DB context for queue/analysis tests.
/// Filesystem-dependent tests (ProcessUnsortedAsync, ConfirmImportAsync) are omitted
/// because they require real directories — those are integration tests.
/// </summary>
public class ImportServiceTests : IDisposable
{
    private readonly ForgeDbContext _db;
    private readonly ImportService _importService;
    private readonly string _tempDir;

    public ImportServiceTests()
    {
        _db = TestDbContextFactory.Create();

        _tempDir = Path.Combine(Path.GetTempPath(), $"forgekeeper-import-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:BasePaths:0"] = _tempDir,
            })
            .Build();

        var logger = new Mock<ILogger<ImportService>>();
        _importService = new ImportService(_db, config, logger.Object);
    }

    [Fact]
    public async Task GetQueueAsync_ReturnsAllItems_WhenNoStatusFilter()
    {
        // Arrange
        _db.ImportQueue.AddRange(
            new ImportQueueItem
            {
                Id = Guid.NewGuid(),
                OriginalPath = "/fake/path1",
                Status = ImportStatus.Pending,
                CreatedAt = DateTime.UtcNow,
            },
            new ImportQueueItem
            {
                Id = Guid.NewGuid(),
                OriginalPath = "/fake/path2",
                Status = ImportStatus.AwaitingReview,
                CreatedAt = DateTime.UtcNow,
            },
            new ImportQueueItem
            {
                Id = Guid.NewGuid(),
                OriginalPath = "/fake/path3",
                Status = ImportStatus.Confirmed,
                CreatedAt = DateTime.UtcNow,
            });
        await _db.SaveChangesAsync();

        // Act
        var result = await _importService.GetQueueAsync();

        // Assert
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetQueueAsync_FiltersByStatus()
    {
        // Arrange
        _db.ImportQueue.AddRange(
            new ImportQueueItem
            {
                Id = Guid.NewGuid(),
                OriginalPath = "/fake/path1",
                Status = ImportStatus.Pending,
                CreatedAt = DateTime.UtcNow,
            },
            new ImportQueueItem
            {
                Id = Guid.NewGuid(),
                OriginalPath = "/fake/path2",
                Status = ImportStatus.AwaitingReview,
                CreatedAt = DateTime.UtcNow,
            });
        await _db.SaveChangesAsync();

        // Act
        var result = await _importService.GetQueueAsync(ImportStatus.Pending);

        // Assert
        Assert.Single(result);
        Assert.Equal(ImportStatus.Pending, result[0].Status);
    }

    [Fact]
    public async Task DismissAsync_RemovesItemFromQueue()
    {
        // Arrange
        var itemId = Guid.NewGuid();
        _db.ImportQueue.Add(new ImportQueueItem
        {
            Id = itemId,
            OriginalPath = "/fake/path",
            Status = ImportStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        // Act
        await _importService.DismissAsync(itemId);

        // Assert
        Assert.Null(await _db.ImportQueue.FindAsync(itemId));
    }

    [Fact]
    public async Task DismissAsync_ThrowsForNonexistentItem()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _importService.DismissAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ProcessUnsortedAsync_WithDirectoryContainingVariantFolders_HigherConfidence()
    {
        // Arrange: Create an unsorted directory with variant subfolders
        var unsortedDir = Path.Combine(_tempDir, "unsorted");
        Directory.CreateDirectory(unsortedDir);

        var modelDir = Path.Combine(unsortedDir, "Cool Dragon Model");
        Directory.CreateDirectory(modelDir);
        Directory.CreateDirectory(Path.Combine(modelDir, "supported"));
        Directory.CreateDirectory(Path.Combine(modelDir, "unsupported"));

        // Add an STL file
        await File.WriteAllTextAsync(
            Path.Combine(modelDir, "supported", "model.stl"), "fake stl content");

        // Act
        var result = await _importService.ProcessUnsortedAsync();

        // Assert
        Assert.Single(result);
        Assert.Equal("Cool Dragon Model", result[0].DetectedModelName);
        Assert.True(result[0].ConfidenceScore >= 0.3,
            $"Confidence should be >= 0.3 for directory with variant folders, got {result[0].ConfidenceScore}");
    }

    [Fact]
    public async Task ProcessUnsortedAsync_WithMetadataJson_HighConfidence()
    {
        // Arrange
        var unsortedDir = Path.Combine(_tempDir, "unsorted");
        Directory.CreateDirectory(unsortedDir);

        var modelDir = Path.Combine(unsortedDir, "Space Marine");
        Directory.CreateDirectory(modelDir);

        var metadata = """
        {
          "metadataVersion": 1,
          "source": "mmf",
          "externalId": "12345",
          "name": "Space Marine Captain",
          "creator": { "displayName": "TestCreator" }
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(modelDir, "metadata.json"), metadata);
        await File.WriteAllTextAsync(Path.Combine(modelDir, "model.stl"), "fake");

        // Act
        var result = await _importService.ProcessUnsortedAsync();

        // Assert
        Assert.Single(result);
        Assert.Equal("Space Marine Captain", result[0].DetectedModelName);
        Assert.Equal("TestCreator", result[0].DetectedCreator);
        Assert.True(result[0].ConfidenceScore >= 0.4,
            $"Confidence should be >= 0.4 with metadata.json, got {result[0].ConfidenceScore}");
    }

    [Fact]
    public async Task ProcessUnsortedAsync_SkipsAlreadyQueuedItems()
    {
        // Arrange
        var unsortedDir = Path.Combine(_tempDir, "unsorted");
        Directory.CreateDirectory(unsortedDir);

        var modelDir = Path.Combine(unsortedDir, "SomeModel");
        Directory.CreateDirectory(modelDir);

        // Pre-add to queue
        _db.ImportQueue.Add(new ImportQueueItem
        {
            Id = Guid.NewGuid(),
            OriginalPath = modelDir,
            Status = ImportStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        // Act
        var result = await _importService.ProcessUnsortedAsync();

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetQueueAsync_ReturnsOrderedByCreatedAtDescending()
    {
        // Arrange
        _db.ImportQueue.AddRange(
            new ImportQueueItem
            {
                Id = Guid.NewGuid(),
                OriginalPath = "/fake/old",
                Status = ImportStatus.Pending,
                CreatedAt = DateTime.UtcNow.AddHours(-2),
            },
            new ImportQueueItem
            {
                Id = Guid.NewGuid(),
                OriginalPath = "/fake/new",
                Status = ImportStatus.Pending,
                CreatedAt = DateTime.UtcNow,
            });
        await _db.SaveChangesAsync();

        // Act
        var result = await _importService.GetQueueAsync();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.True(result[0].CreatedAt >= result[1].CreatedAt);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
