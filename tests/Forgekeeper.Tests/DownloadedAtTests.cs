using System.Text.Json;
using Forgekeeper.Core.Models;
using Forgekeeper.PluginSdk;
using Forgekeeper.Scraper.Mmf;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>#76: MMF-synced rows must get models.downloaded_at.</summary>
public class DownloadedAtTests
{
    private static SourceMetadata? RoundTrip(Dictionary<string, object?> metadata) =>
        JsonSerializer.Deserialize<SourceMetadata>(JsonSerializer.Serialize(metadata));

    [Fact]
    public void BuildMetadata_WritesDatesDownloaded_ThatTheScannerReads()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var model = new ScrapedModel { ExternalId = "object-123", Name = "Test" };

        var parsed = RoundTrip(MmfScraperPlugin.BuildMetadata(model, null, new List<DownloadedFile>()));

        Assert.NotNull(parsed?.Dates?.Downloaded);
        Assert.True(parsed!.Dates!.Downloaded >= before);
    }

    [Fact]
    public void BuildMetadata_PreservesOriginalDownloadedOnResync()
    {
        var original = new DateTime(2026, 4, 16, 15, 1, 22, DateTimeKind.Utc);
        var existingJson = JsonSerializer.Serialize(new { dates = new { downloaded = original } });
        var existing = JsonSerializer.Deserialize<Dictionary<string, object?>>(existingJson);
        var model = new ScrapedModel { ExternalId = "object-123", Name = "Test" };

        var parsed = RoundTrip(MmfScraperPlugin.BuildMetadata(model, null, new List<DownloadedFile>(), existing));

        Assert.Equal(original, parsed!.Dates!.Downloaded!.Value.ToUniversalTime());
    }
}
