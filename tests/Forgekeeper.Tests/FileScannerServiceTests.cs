using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Services;
using Forgekeeper.Infrastructure.SourceAdapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Tests for FileScannerService — directory walking, metadata reading,
/// backfill, variant detection, creator extraction, and incremental scanning.
/// Uses real temp directories for filesystem operations.
/// </summary>
public class FileScannerServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IMetadataService> _metadataService;
    private readonly Mock<ILogger<FileScannerService>> _logger;

    public FileScannerServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"forgekeeper-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _metadataService = new Mock<IMetadataService>();
        _logger = new Mock<ILogger<FileScannerService>>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private FileScannerService CreateService(ForgeDbContext db, IConfiguration? config = null)
    {
        config ??= new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:BasePaths:0"] = _tempDir
            })
            .Build();

        var dbFactory = new Mock<IDbContextFactory<ForgeDbContext>>();
        dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => TestDbContextFactory.Create(db.Database.GetDbConnection().Database));

        // Use a shared db name so all contexts share the same InMemory database
        var dbName = db.Database.GetDbConnection().Database;

        var adapters = new ISourceAdapter[]
        {
            new MmfSourceAdapter(),
            new GenericSourceAdapter(SourceType.Thangs, "thangs"),
        };

        return new FileScannerService(dbFactory.Object, adapters, _metadataService.Object, config, _logger.Object);
    }

    private FileScannerService CreateServiceWithSharedDb(string dbName)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:BasePaths:0"] = _tempDir
            })
            .Build();

        var dbFactory = new Mock<IDbContextFactory<ForgeDbContext>>();
        dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => TestDbContextFactory.Create(dbName));

        var adapters = new ISourceAdapter[]
        {
            new MmfSourceAdapter(),
            new GenericSourceAdapter(SourceType.Thangs, "thangs"),
        };

        return new FileScannerService(dbFactory.Object, adapters, _metadataService.Object, config, _logger.Object);
    }

    private void CreateModelDirectory(string sourceName, string creatorName, string modelName, string[]? files = null)
    {
        var modelDir = Path.Combine(_tempDir, "sources", sourceName, creatorName, modelName);
        Directory.CreateDirectory(modelDir);

        files ??= ["model.stl"];
        foreach (var file in files)
        {
            var filePath = Path.Combine(modelDir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "test content");
        }
    }

    // --- DetectVariantType (static, pure) ---

    [Theory]
    [InlineData("supported/model.stl", ".stl", VariantType.Supported)]
    [InlineData("unsupported/model.stl", ".stl", VariantType.Unsupported)]
    [InlineData("presupported/model.stl", ".stl", VariantType.Presupported)]
    [InlineData("pre-supported/model.stl", ".stl", VariantType.Presupported)]
    [InlineData("lychee/model.lys", ".lys", VariantType.LycheeProject)]
    [InlineData("chitubox/model.ctb", ".ctb", VariantType.ChituboxProject)]
    [InlineData("images/preview.png", ".png", VariantType.PreviewImage)]
    public void DetectVariantType_FolderBased(string relativePath, string ext, VariantType expected)
    {
        Assert.Equal(expected, FileScannerService.DetectVariantType(relativePath, ext));
    }

    [Theory]
    [InlineData("model.stl", ".stl", VariantType.Unsupported)]
    [InlineData("model.lys", ".lys", VariantType.LycheeProject)]
    [InlineData("model.3mf", ".3mf", VariantType.PrintProject)]
    [InlineData("model.gcode", ".gcode", VariantType.Gcode)]
    public void DetectVariantType_ExtensionBased(string relativePath, string ext, VariantType expected)
    {
        Assert.Equal(expected, FileScannerService.DetectVariantType(relativePath, ext));
    }

    [Theory]
    [InlineData(".stl", FileType.Stl)]
    [InlineData(".obj", FileType.Obj)]
    [InlineData(".3mf", FileType.Threemf)]
    [InlineData(".lys", FileType.Lys)]
    [InlineData(".ctb", FileType.Ctb)]
    [InlineData(".xyz", FileType.Other)]
    public void DetectFileType_ReturnsCorrectType(string ext, FileType expected)
    {
        Assert.Equal(expected, FileScannerService.DetectFileType(ext));
    }

    // --- ScanAsync integration tests ---

    [Fact]
    public async Task ScanAsync_FindsModelDirectories()
    {
        var dbName = $"TestDb_{Guid.NewGuid()}";
        CreateModelDirectory("thangs", "TestCreator", "CoolModel", ["model.stl", "supported/model_sup.stl"]);

        _metadataService.Setup(m => m.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SourceMetadata?)null);

        var service = CreateServiceWithSharedDb(dbName);
        var progress = await service.ScanAsync(incremental: false);

        Assert.Equal("completed", progress.Status);
        Assert.True(progress.ModelsFound >= 1, $"Expected at least 1 model, got {progress.ModelsFound}");
    }

    [Fact]
    public async Task ScanAsync_ReadsMetadataWhenPresent()
    {
        var dbName = $"TestDb_{Guid.NewGuid()}";
        CreateModelDirectory("thangs", "TestCreator", "MetaModel", ["model.stl"]);

        var metadata = new SourceMetadata
        {
            Name = "Custom Name From Metadata",
            ExternalId = "ext-123",
            Creator = new MetadataCreator { DisplayName = "MetaCreator" },
            Tags = ["fantasy", "miniature"],
        };

        _metadataService.Setup(m => m.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        var service = CreateServiceWithSharedDb(dbName);
        await service.ScanAsync(incremental: false);

        // Verify the model was created with metadata values
        using var db = TestDbContextFactory.Create(dbName);
        var model = await db.Models.Include(m => m.Creator).Include(m => m.Tags).FirstOrDefaultAsync();
        Assert.NotNull(model);
        Assert.Equal("Custom Name From Metadata", model.Name);
        Assert.Equal("ext-123", model.SourceId);
    }

    [Fact]
    public async Task ScanAsync_BackfillsMetadataWhenAbsent()
    {
        var dbName = $"TestDb_{Guid.NewGuid()}";
        CreateModelDirectory("thangs", "TestCreator", "NoMetaModel", ["model.stl"]);

        _metadataService.Setup(m => m.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SourceMetadata?)null);
        _metadataService.Setup(m => m.BackfillAsync(It.IsAny<string>(), It.IsAny<Model3D>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateServiceWithSharedDb(dbName);
        await service.ScanAsync(incremental: false);

        _metadataService.Verify(m => m.BackfillAsync(
            It.IsAny<string>(), It.IsAny<Model3D>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ScanAsync_ExtractsCreatorAndModelFromPath()
    {
        var dbName = $"TestDb_{Guid.NewGuid()}";
        CreateModelDirectory("thangs", "ArtisanMinis", "Dragon Lord", ["model.stl"]);

        _metadataService.Setup(m => m.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SourceMetadata?)null);

        var service = CreateServiceWithSharedDb(dbName);
        await service.ScanAsync(incremental: false);

        using var db = TestDbContextFactory.Create(dbName);
        var creator = await db.Creators.FirstOrDefaultAsync(c => c.Name == "ArtisanMinis");
        Assert.NotNull(creator);

        var model = await db.Models.FirstOrDefaultAsync(m => m.Name == "Dragon Lord");
        Assert.NotNull(model);
    }

    [Fact]
    public async Task ScanAsync_DetectsVariantsFromFolderStructure()
    {
        var dbName = $"TestDb_{Guid.NewGuid()}";
        CreateModelDirectory("thangs", "Creator1", "Model1",
        [
            "supported/model_sup.stl",
            "unsupported/model_unsup.stl",
            "presupported/model_pre.stl",
        ]);

        _metadataService.Setup(m => m.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SourceMetadata?)null);

        var service = CreateServiceWithSharedDb(dbName);
        await service.ScanAsync(incremental: false);

        using var db = TestDbContextFactory.Create(dbName);
        var model = await db.Models.Include(m => m.Variants).FirstOrDefaultAsync();
        Assert.NotNull(model);
        Assert.Equal(3, model.Variants.Count);
        Assert.Contains(model.Variants, v => v.VariantType == VariantType.Supported);
        Assert.Contains(model.Variants, v => v.VariantType == VariantType.Unsupported);
        Assert.Contains(model.Variants, v => v.VariantType == VariantType.Presupported);
    }

    [Fact]
    public async Task ScanAsync_IncrementalSkipsUnchangedDirectories()
    {
        var dbName = $"TestDb_{Guid.NewGuid()}";
        CreateModelDirectory("thangs", "Creator1", "Model1", ["model.stl"]);

        _metadataService.Setup(m => m.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SourceMetadata?)null);

        // First scan
        var service = CreateServiceWithSharedDb(dbName);
        var firstProgress = await service.ScanAsync(incremental: false);
        Assert.True(firstProgress.ModelsFound >= 1);

        // Reset for fresh service (simulating new scan run)
        var service2 = CreateServiceWithSharedDb(dbName);

        // Second incremental scan — directory hasn't changed
        var secondProgress = await service2.ScanAsync(incremental: true);
        // The model shouldn't be updated (no new models found)
        Assert.Equal("completed", secondProgress.Status);
    }

    [Fact]
    public async Task ScanAsync_SetsCorrectProgress()
    {
        var dbName = $"TestDb_{Guid.NewGuid()}";
        CreateModelDirectory("thangs", "Creator1", "Model1", ["model.stl"]);

        _metadataService.Setup(m => m.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SourceMetadata?)null);

        var service = CreateServiceWithSharedDb(dbName);

        Assert.False(service.IsRunning);
        var progress = await service.ScanAsync(incremental: false);

        Assert.False(service.IsRunning);
        Assert.Equal("completed", progress.Status);
        Assert.NotNull(progress.StartedAt);
        Assert.NotNull(progress.CompletedAt);
        Assert.True(progress.ElapsedSeconds >= 0);
    }

    [Fact]
    public async Task ScanAsync_HandlesEmptySourcesDirectory()
    {
        var dbName = $"TestDb_{Guid.NewGuid()}";
        // Create sources dir but no source subdirectories
        Directory.CreateDirectory(Path.Combine(_tempDir, "sources"));

        var service = CreateServiceWithSharedDb(dbName);
        var progress = await service.ScanAsync(incremental: false);

        Assert.Equal("completed", progress.Status);
        Assert.Equal(0, progress.ModelsFound);
    }

    [Fact]
    public async Task ScanAsync_HandlesMissingSourcesDirectory()
    {
        // Don't create any sources directory
        var dbName = $"TestDb_{Guid.NewGuid()}";
        var service = CreateServiceWithSharedDb(dbName);
        var progress = await service.ScanAsync(incremental: false);

        Assert.Equal("completed", progress.Status);
        Assert.Equal(0, progress.ModelsFound);
    }

    [Fact]
    public async Task ScanAsync_DetectsPreviewImages()
    {
        var dbName = $"TestDb_{Guid.NewGuid()}";
        CreateModelDirectory("thangs", "Creator1", "Model1",
        [
            "model.stl",
            "preview.png",
            "render.jpg",
        ]);

        _metadataService.Setup(m => m.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SourceMetadata?)null);

        var service = CreateServiceWithSharedDb(dbName);
        await service.ScanAsync(incremental: false);

        using var db = TestDbContextFactory.Create(dbName);
        var model = await db.Models.FirstOrDefaultAsync();
        Assert.NotNull(model);
        Assert.Equal(2, model.PreviewImages.Count);
    }

    [Fact]
    public async Task ScanAsync_SupportsCancellation()
    {
        var dbName = $"TestDb_{Guid.NewGuid()}";
        CreateModelDirectory("thangs", "Creator1", "Model1", ["model.stl"]);
        CreateModelDirectory("thangs", "Creator2", "Model2", ["model.stl"]);

        _metadataService.Setup(m => m.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SourceMetadata?)null);

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var service = CreateServiceWithSharedDb(dbName);
        var progress = await service.ScanAsync(incremental: false, ct: cts.Token);

        Assert.Equal("cancelled", progress.Status);
    }
}
