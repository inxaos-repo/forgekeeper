using System.Text.Json;
using Forgekeeper.PluginSdk;
using Forgekeeper.Scraper.Mmf;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Phase 3 tests: update detection, metadata preservation, tag merging, lastSynced tracking.
/// </summary>
public class Phase3Tests
{
    // --- Metadata Preservation ---

    [Fact]
    public void BuildMetadata_PreservesUserRating()
    {
        var model = new ScrapedModel { ExternalId = "123", Name = "Test" };
        var existing = new Dictionary<string, object?> { ["rating"] = 4 };

        var result = MmfScraperPlugin.BuildMetadata(model, null, new List<DownloadedFile>(), existing);

        Assert.Equal(4, result["rating"]);
    }

    [Fact]
    public void BuildMetadata_PreservesPrintHistory()
    {
        var model = new ScrapedModel { ExternalId = "123", Name = "Test" };
        var printHistory = new List<object> { "2026-01-15 printed on Bambu A1" };
        var existing = new Dictionary<string, object?> { ["printHistory"] = printHistory };

        var result = MmfScraperPlugin.BuildMetadata(model, null, new List<DownloadedFile>(), existing);

        Assert.NotNull(result["printHistory"]);
    }

    [Fact]
    public void BuildMetadata_PreservesNotes()
    {
        var model = new ScrapedModel { ExternalId = "123", Name = "Test" };
        var existing = new Dictionary<string, object?> { ["notes"] = "Great model, needs supports on the arms" };

        var result = MmfScraperPlugin.BuildMetadata(model, null, new List<DownloadedFile>(), existing);

        Assert.Equal("Great model, needs supports on the arms", result["notes"]);
    }

    [Fact]
    public void BuildMetadata_PreservesGameSystem()
    {
        var model = new ScrapedModel { ExternalId = "123", Name = "Test" };
        var existing = new Dictionary<string, object?>
        {
            ["scale"] = "28mm",
            ["gameSystem"] = "Warhammer 40K",
            ["collection"] = "Grimdark Future",
        };

        var result = MmfScraperPlugin.BuildMetadata(model, null, new List<DownloadedFile>(), existing);

        Assert.Equal("28mm", result["scale"]);
        Assert.Equal("Warhammer 40K", result["gameSystem"]);
        Assert.Equal("Grimdark Future", result["collection"]);
    }

    [Fact]
    public void BuildMetadata_WithoutExisting_NoUserFields()
    {
        var model = new ScrapedModel { ExternalId = "123", Name = "Test" };
        var result = MmfScraperPlugin.BuildMetadata(model, null, new List<DownloadedFile>());

        Assert.False(result.ContainsKey("rating"));
        Assert.False(result.ContainsKey("notes"));
        Assert.False(result.ContainsKey("printHistory"));
    }

    // --- Tag Merging ---

    [Fact]
    public void BuildMetadata_MergesSourceAndUserTags()
    {
        var model = new ScrapedModel { ExternalId = "123", Name = "Test" };
        var details = new MmfModelDetails
        {
            Tags = new List<MmfTag> { new() { Name = "fantasy" }, new() { Name = "dragon" } }
        };
        var existing = new Dictionary<string, object?>
        {
            ["userTags"] = JsonSerializer.SerializeToElement(new[] { "warhammer", "painted" })
        };

        var result = MmfScraperPlugin.BuildMetadata(model, details, new List<DownloadedFile>(), existing);

        var tags = result["tags"] as List<string?>;
        Assert.NotNull(tags);
        Assert.Contains("fantasy", tags);
        Assert.Contains("dragon", tags);
        Assert.Contains("warhammer", tags);
        Assert.Contains("painted", tags);
    }

    [Fact]
    public void BuildMetadata_DeduplicatesTags()
    {
        var model = new ScrapedModel { ExternalId = "123", Name = "Test" };
        var details = new MmfModelDetails
        {
            Tags = new List<MmfTag> { new() { Name = "fantasy" }, new() { Name = "Dragon" } }
        };
        var existing = new Dictionary<string, object?>
        {
            ["userTags"] = JsonSerializer.SerializeToElement(new[] { "Fantasy", "knight" })
        };

        var result = MmfScraperPlugin.BuildMetadata(model, details, new List<DownloadedFile>(), existing);

        var tags = result["tags"] as List<string?>;
        Assert.NotNull(tags);
        // "fantasy" and "Fantasy" should be deduped (case-insensitive)
        var fantasyCount = tags.Count(t => t?.Equals("fantasy", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal(1, fantasyCount);
    }

    [Fact]
    public void BuildMetadata_TracksSourceTags()
    {
        var model = new ScrapedModel { ExternalId = "123", Name = "Test" };
        var details = new MmfModelDetails
        {
            Tags = new List<MmfTag> { new() { Name = "fantasy" } }
        };

        var result = MmfScraperPlugin.BuildMetadata(model, details, new List<DownloadedFile>());

        var sourceTags = result["sourceTags"] as List<string?>;
        Assert.NotNull(sourceTags);
        Assert.Contains("fantasy", sourceTags);
    }

    // --- LastSynced Tracking ---

    [Fact]
    public void BuildMetadata_SetsLastSynced()
    {
        var model = new ScrapedModel { ExternalId = "123", Name = "Test" };
        var result = MmfScraperPlugin.BuildMetadata(model, null, new List<DownloadedFile>());

        var dates = result["dates"] as Dictionary<string, object?>;
        Assert.NotNull(dates);
        Assert.True(dates.ContainsKey("lastSynced"));
        Assert.NotNull(dates["lastSynced"]);
    }

    // --- MetadataVersion ---

    [Fact]
    public void BuildMetadata_SetsVersion3()
    {
        var model = new ScrapedModel { ExternalId = "123", Name = "Test" };
        var result = MmfScraperPlugin.BuildMetadata(model, null, new List<DownloadedFile>());

        Assert.Equal(3, result["metadataVersion"]);
    }

    // --- User Description Preservation ---

    [Fact]
    public void BuildMetadata_PreservesUserDescription()
    {
        var model = new ScrapedModel { ExternalId = "123", Name = "Test" };
        var details = new MmfModelDetails { Description = "MMF description from source" };
        var existing = new Dictionary<string, object?>
        {
            ["userDescription"] = "My custom description that I wrote"
        };

        var result = MmfScraperPlugin.BuildMetadata(model, details, new List<DownloadedFile>(), existing);

        // User description should win
        Assert.Equal("My custom description that I wrote", result["description"]);
        Assert.Equal("My custom description that I wrote", result["userDescription"]);
    }

    [Fact]
    public void BuildMetadata_UsesSourceDescriptionWhenNoUserEdit()
    {
        var model = new ScrapedModel { ExternalId = "123", Name = "Test" };
        var details = new MmfModelDetails { Description = "Source description" };

        var result = MmfScraperPlugin.BuildMetadata(model, details, new List<DownloadedFile>());

        Assert.Equal("Source description", result["description"]);
        Assert.False(result.ContainsKey("userDescription"));
    }
}
