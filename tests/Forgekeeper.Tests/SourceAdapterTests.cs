using Xunit;
using Forgekeeper.Core.Enums;
using Forgekeeper.Infrastructure.SourceAdapters;

namespace Forgekeeper.Tests;

public class SourceAdapterTests
{
    [Fact]
    public void MmfAdapter_CanHandle_SourcesMmfPath()
    {
        var adapter = new MmfSourceAdapter();
        Assert.True(adapter.CanHandle("/mnt/3dprinting/sources/mmf/Creator/Model - 12345"));
    }

    [Fact]
    public void MmfAdapter_CanHandle_MMFDownloaderPath()
    {
        var adapter = new MmfSourceAdapter();
        Assert.True(adapter.CanHandle("/mnt/3dprinting/MMFDownloader/Creator/Model"));
    }

    [Fact]
    public void MmfAdapter_CannotHandle_OtherSource()
    {
        var adapter = new MmfSourceAdapter();
        Assert.False(adapter.CanHandle("/mnt/3dprinting/sources/thangs/Creator/Model"));
    }

    [Fact]
    public void MmfAdapter_ParsesModelIdFromFolderName()
    {
        // Create temp dirs to simulate real filesystem
        var tempDir = Path.Combine(Path.GetTempPath(), "forgekeeper-test", "Creator");
        var modelDir = Path.Combine(tempDir, "Space Marine Captain - 12345");
        Directory.CreateDirectory(modelDir);

        try
        {
            var adapter = new MmfSourceAdapter();
            var result = adapter.ParseModelDirectory(modelDir);

            Assert.NotNull(result);
            Assert.Equal("Creator", result.CreatorName);
            Assert.Equal("Space Marine Captain", result.ModelName);
            Assert.Equal("12345", result.SourceId);
            Assert.Equal(SourceType.Mmf, result.Source);
        }
        finally
        {
            Directory.Delete(Path.Combine(Path.GetTempPath(), "forgekeeper-test"), recursive: true);
        }
    }

    [Fact]
    public void MmfAdapter_HandlesModelWithoutId()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "forgekeeper-test2", "Creator");
        var modelDir = Path.Combine(tempDir, "Some Model Name");
        Directory.CreateDirectory(modelDir);

        try
        {
            var adapter = new MmfSourceAdapter();
            var result = adapter.ParseModelDirectory(modelDir);

            Assert.NotNull(result);
            Assert.Equal("Creator", result.CreatorName);
            Assert.Equal("Some Model Name", result.ModelName);
            Assert.Null(result.SourceId);
        }
        finally
        {
            Directory.Delete(Path.Combine(Path.GetTempPath(), "forgekeeper-test2"), recursive: true);
        }
    }

    [Fact]
    public void GenericAdapter_CanHandle_CorrectSource()
    {
        var adapter = new GenericSourceAdapter(SourceType.Thangs, "thangs");
        Assert.True(adapter.CanHandle("/mnt/3dprinting/sources/thangs/Creator/Model"));
        Assert.False(adapter.CanHandle("/mnt/3dprinting/sources/mmf/Creator/Model"));
    }

    [Fact]
    public void GenericAdapter_ParsesCreatorAndModel()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "forgekeeper-test3", "AwesomeCreator");
        var modelDir = Path.Combine(tempDir, "Cool Dragon");
        Directory.CreateDirectory(modelDir);

        try
        {
            var adapter = new GenericSourceAdapter(SourceType.Thangs, "thangs");
            var result = adapter.ParseModelDirectory(modelDir);

            Assert.NotNull(result);
            Assert.Equal("AwesomeCreator", result.CreatorName);
            Assert.Equal("Cool Dragon", result.ModelName);
            Assert.Equal(SourceType.Thangs, result.Source);
        }
        finally
        {
            Directory.Delete(Path.Combine(Path.GetTempPath(), "forgekeeper-test3"), recursive: true);
        }
    }

    [Fact]
    public void PatreonAdapter_CanHandle_SourcesPatreonPath()
    {
        var adapter = new PatreonSourceAdapter();
        Assert.True(adapter.CanHandle("/mnt/3dprinting/sources/patreon/Creator/2025-06 June Release/Model"));
    }
}
