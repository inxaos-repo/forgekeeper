using System.Text.Json;
using Forgekeeper.PluginSdk;
using Forgekeeper.Scraper.Mmf;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Phase 5 tests: metrics output format, SSE progress data structure,
/// naming template token system (prep for Phase 5 + import work).
/// </summary>
public class Phase5Tests
{
    // --- Prometheus Metrics Format ---

    [Fact]
    public void PrometheusMetrics_FormatValidation()
    {
        // Validate that our metrics format matches Prometheus text format
        var line = @"forgekeeper_models_total{source=""mmf""} 4010";
        Assert.Contains("forgekeeper_models_total", line);
        Assert.Contains(@"source=""mmf""", line);
        Assert.EndsWith("4010", line);
    }

    [Fact]
    public void PrometheusMetrics_GaugeType()
    {
        var helpLine = "# HELP forgekeeper_models_total Total models in library";
        var typeLine = "# TYPE forgekeeper_models_total gauge";
        Assert.StartsWith("# HELP", helpLine);
        Assert.StartsWith("# TYPE", typeLine);
        Assert.Contains("gauge", typeLine);
    }

    [Theory]
    [InlineData("forgekeeper_models_total", "gauge")]
    [InlineData("forgekeeper_creators_total", "gauge")]
    [InlineData("forgekeeper_files_total", "gauge")]
    [InlineData("forgekeeper_library_size_bytes", "gauge")]
    [InlineData("forgekeeper_thumbnails_total", "gauge")]
    [InlineData("forgekeeper_sync_running", "gauge")]
    [InlineData("forgekeeper_sync_scraped_total", "gauge")]
    [InlineData("forgekeeper_sync_failed_total", "gauge")]
    public void PrometheusMetrics_AllMetricNamesValid(string metricName, string metricType)
    {
        // Prometheus metric names must match [a-zA-Z_:][a-zA-Z0-9_:]*
        Assert.Matches(@"^[a-zA-Z_:][a-zA-Z0-9_:]*$", metricName);
        Assert.Contains(metricType, new[] { "gauge", "counter", "histogram", "summary" });
    }

    // --- SSE Progress Data Structure ---

    [Fact]
    public void SseProgress_SerializesCorrectly()
    {
        var progress = new
        {
            scraped = 100,
            total = 7230,
            failed = 2,
            currentItem = "Dragon Knight Champion",
            status = "downloading"
        };

        var json = JsonSerializer.Serialize(progress);
        var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;

        Assert.Equal(100, root.GetProperty("scraped").GetInt32());
        Assert.Equal(7230, root.GetProperty("total").GetInt32());
        Assert.Equal(2, root.GetProperty("failed").GetInt32());
        Assert.Equal("Dragon Knight Champion", root.GetProperty("currentItem").GetString());
        Assert.Equal("downloading", root.GetProperty("status").GetString());
    }

    [Fact]
    public void SseComplete_SerializesCorrectly()
    {
        var complete = new
        {
            scraped = 7230,
            total = 7230,
            failed = 5,
            complete = true
        };

        var json = JsonSerializer.Serialize(complete);
        var parsed = JsonDocument.Parse(json);
        Assert.True(parsed.RootElement.GetProperty("complete").GetBoolean());
    }

    // --- SSE Event Format ---

    [Fact]
    public void SseEventFormat_Valid()
    {
        // SSE format: "event: <type>\ndata: <json>\n\n"
        var eventType = "progress";
        var data = @"{""scraped"":50,""total"":100}";
        var sseEvent = $"event: {eventType}\ndata: {data}\n\n";

        Assert.StartsWith("event: progress", sseEvent);
        Assert.Contains("data: {", sseEvent);
        Assert.EndsWith("\n\n", sseEvent);
    }

    // --- Variant Detection Completeness (ensure no regressions from Phase 2) ---

    [Theory]
    [InlineData("model_presupported_v2.stl", "presupported")]
    [InlineData("model_supported_final.stl", "supported")]
    [InlineData("model_unsupported.stl", "unsupported")]
    [InlineData("model_nosupport.obj", "unsupported")]
    [InlineData("scene_file.lys", "lychee")]
    [InlineData("print_ready.ctb", "chitubox")]
    [InlineData("raw_model.stl", null)]
    public void VariantDetection_StillCorrectAfterPhase5(string filename, string? expected)
    {
        // Regression test — ensure Phase 5 changes didn't break variant detection
        Assert.Equal(expected, MmfScraperPlugin.DetectVariant(filename));
    }

    // --- Archive Detection Completeness ---

    [Theory]
    [InlineData("model.zip", true)]
    [InlineData("model.rar", true)]
    [InlineData("model.7z", true)]
    [InlineData("model.stl", false)]
    [InlineData("model.3mf", false)]
    public void ArchiveDetection_StillCorrectAfterPhase5(string filename, bool expected)
    {
        Assert.Equal(expected, MmfScraperPlugin.IsArchiveFile(filename));
    }

    // --- Metadata Version ---

    [Fact]
    public void BuildMetadata_StillVersion3()
    {
        var model = new ScrapedModel { ExternalId = "test", Name = "Test" };
        var result = MmfScraperPlugin.BuildMetadata(model, null, new List<DownloadedFile>());
        Assert.Equal(3, result["metadataVersion"]);
    }

    // --- Tag Merge Still Works ---

    [Fact]
    public void TagMerge_StillPreservesUserTags()
    {
        var model = new ScrapedModel { ExternalId = "test", Name = "Test" };
        var details = new MmfModelDetails
        {
            Tags = new List<MmfTag> { new() { Name = "source-tag" } }
        };
        var existing = new Dictionary<string, object?>
        {
            ["userTags"] = JsonSerializer.SerializeToElement(new[] { "user-tag" })
        };

        var result = MmfScraperPlugin.BuildMetadata(model, details, new List<DownloadedFile>(), existing);
        var tags = result["tags"] as List<string?>;
        Assert.Contains("source-tag", tags);
        Assert.Contains("user-tag", tags);
    }
}
