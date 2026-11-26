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

/// <summary>#110: pack layouts nested deeper than creator/model become one model per leaf folder.</summary>
public class NestedModelDiscoveryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"fk-nested-{Guid.NewGuid():N}");
    private readonly string _dbName = $"TestDb_{Guid.NewGuid()}";
    private readonly Mock<IMetadataService> _metadata = new();

    public NestedModelDiscoveryTests()
    {
        Directory.CreateDirectory(_tempDir);
        _metadata.Setup(m => m.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SourceMetadata?)null);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    private FileScannerService CreateService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:BasePaths:0"] = _tempDir })
            .Build();
        var dbFactory = new Mock<IDbContextFactory<ForgeDbContext>>();
        dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => TestDbContextFactory.Create(_dbName));
        var adapters = new ISourceAdapter[]
        {
            new MmfSourceAdapter(),
            new GenericSourceAdapter(SourceType.Patreon, "patreon"),
            new GenericSourceAdapter(SourceType.Manual, "manual"),
        };
        return new FileScannerService(dbFactory.Object, adapters, _metadata.Object, config,
            new Mock<ILogger<FileScannerService>>().Object);
    }

    private void Touch(string relPath)
    {
        var full = Path.Combine(_tempDir, "sources", relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
    }

    private async Task<List<Model3D>> ModelsAsync()
    {
        using var db = TestDbContextFactory.Create(_dbName);
        return await db.Models.Include(m => m.Creator).OrderBy(m => m.BasePath).ToListAsync();
    }

    [Fact]
    public async Task Ks3200Layout_EachModelFolderBecomesAModel()
    {
        Touch("manual/KS 3200/7. EGYPT & THE TOMBS/models/model_009/a.stl");
        Touch("manual/KS 3200/7. EGYPT & THE TOMBS/models/model_009/b.stl");
        Touch("manual/KS 3200/7. EGYPT & THE TOMBS/models/model_010/a.stl");

        var p = await CreateService().ScanAsync();

        var models = await ModelsAsync();
        Assert.Equal(2, models.Count);
        Assert.All(models, m => Assert.Equal("KS 3200", m.Creator.Name));
        Assert.Equal("7. EGYPT & THE TOMBS / model_009", models[0].Name);
        Assert.EndsWith("model_009", models[0].BasePath);
        Assert.True(models[0].FileCount > models[1].FileCount);
        Assert.Equal(1, p.NestedContainers);
    }

    [Fact]
    public async Task Vol6Layout_FiguresSubfolders()
    {
        Touch("manual/vol6 3000/02_Trench/FIGURES/0265/x.stl");
        Touch("manual/vol6 3000/02_Trench/FIGURES/0266/x.obj");

        await CreateService().ScanAsync();

        var models = await ModelsAsync();
        Assert.Equal(new[] { "02_Trench / 0265", "02_Trench / 0266" }, models.Select(m => m.Name));
        Assert.All(models, m => Assert.Equal("vol6 3000", m.Creator.Name));
    }

    [Fact]
    public async Task WitchwoodLayout_FlatModelsUnchanged()
    {
        Touch("manual/Whispers of the Witchwood/Ivy Witchling/ivy.stl");
        Touch("manual/Whispers of the Witchwood/Helga, The Bargain Witch/helga.stl");
        Touch("manual/Whispers of the Witchwood/Helga, The Bargain Witch/supported/helga_sup.stl");

        var p = await CreateService().ScanAsync();

        var models = await ModelsAsync();
        Assert.Equal(new[] { "Helga, The Bargain Witch", "Ivy Witchling" }, models.Select(m => m.Name));
        Assert.True(models[0].FileCount > models[1].FileCount);
        Assert.Equal(0, p.NestedContainers);
    }

    [Fact]
    public async Task VariantOnlySubfolders_StayOneModel()
    {
        Touch("manual/Creator/Dragon/supported/d.stl");
        Touch("manual/Creator/Dragon/unsupported/d.stl");
        Touch("manual/Creator/Knight/Body/b.stl");
        Touch("manual/Creator/Knight/Weapons/w.stl");

        await CreateService().ScanAsync();

        var models = await ModelsAsync();
        Assert.Equal(new[] { "Dragon", "Knight" }, models.Select(m => m.Name));
        Assert.Equal(models[0].FileCount, models[1].FileCount);
        Assert.True(models[0].FileCount >= 2);
    }

    [Fact]
    public async Task MmfAndPatreon_DeepLayoutsNotSplit()
    {
        Touch("mmf/Creator/Pack/models/m1/a.stl");
        Touch("mmf/Creator/Pack/models/m2/a.stl");
        Touch("patreon/Creator/Release/Model/models/m1/a.stl");

        await CreateService().ScanAsync();

        var models = await ModelsAsync();
        Assert.Equal(new[] { "Pack", "Model" }.OrderBy(x => x), models.Select(m => m.Name).OrderBy(x => x));
    }

    [Fact]
    public async Task ExistingContainerRow_NotDeleted_CountedAsSuperseded()
    {
        Touch("manual/KS 3200/7. EGYPT & THE TOMBS/a.stl");
        await CreateService().ScanAsync();
        Assert.Single(await ModelsAsync());

        // Pack re-extracted into the nested layout
        File.Delete(Path.Combine(_tempDir, "sources/manual/KS 3200/7. EGYPT & THE TOMBS/a.stl"));
        Touch("manual/KS 3200/7. EGYPT & THE TOMBS/models/model_009/a.stl");
        Touch("manual/KS 3200/7. EGYPT & THE TOMBS/models/model_010/a.stl");
        var p = await CreateService().ScanAsync();

        Assert.Equal(3, (await ModelsAsync()).Count);
        Assert.Equal(1, p.SupersededContainerRows);
    }

    [Fact]
    public async Task Rescan_IsIdempotent()
    {
        Touch("manual/KS 3200/7. EGYPT & THE TOMBS/models/model_009/a.stl");
        Touch("manual/KS 3200/7. EGYPT & THE TOMBS/models/model_010/a.stl");
        await CreateService().ScanAsync();
        await CreateService().ScanAsync();
        Assert.Equal(2, (await ModelsAsync()).Count);
    }

    [Fact]
    public void FindNested_RespectsDepthLimit()
    {
        Touch("manual/C/Pack/a/b/c/d/e/x.stl");
        var dir = Path.Combine(_tempDir, "sources/manual/C/Pack");
        var leaf = Assert.Single(FileScannerService.FindNestedModelDirs(dir, 4));
        Assert.Equal(Path.Combine(dir, "a", "b", "c", "d"), leaf);
    }
}
