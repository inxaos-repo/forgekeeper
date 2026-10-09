using System.Text.Json;
using Forgekeeper.Infrastructure.Services;
using Forgekeeper.PluginSdk;
using Forgekeeper.Scraper.Mmf;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// sync_run 3c6fa480: every item failed "No download_url/archive_download_url" because MMF returns
/// archive_download_url = null for some objects; the links live in files.items[].download_url.
/// Fixtures are the real /api/v2/objects/{id} shape (2026-10-09) with values redacted.
/// </summary>
public class MmfDownloadUrlResolutionTests
{
    private static JsonDocument Load(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mmf", name)));

    [Fact]
    public void NullArchiveUrl_FallsBackToFileItems()
    {
        using var doc = Load("object-files-only.redacted.json");
        var t = MmfScraperPlugin.ResolveDownloadTargets(doc.RootElement, "My Model");
        var only = Assert.Single(t);
        Assert.StartsWith("https://www.myminifactory.com/download/852561?archive_id=", only.Url);
        Assert.Equal("My Model.zip", only.FileName); // single archive keeps the historical name
        Assert.True(only.IsArchive);
        Assert.Equal(3681304687L, only.ExpectedSize);
    }

    [Fact]
    public void ArchiveUrl_IsPreferredOverParts()
    {
        using var doc = Load("object-archive-and-parts.redacted.json");
        var t = MmfScraperPlugin.ResolveDownloadTargets(doc.RootElement, "Bought");
        var only = Assert.Single(t);
        Assert.Equal("https://www.myminifactory.com/download/802018", only.Url);
        Assert.Equal("Bought.zip", only.FileName);
    }

    [Fact]
    public void MultipleParts_WithoutArchive_AllDownloaded_RelativeUrlsAbsolutized()
    {
        using var src = Load("object-archive-and-parts.redacted.json");
        var json = src.RootElement.GetRawText().Replace("\"archive_download_url\":\"https://www.myminifactory.com/download/802018\"", "\"archive_download_url\":null");
        using var doc = JsonDocument.Parse(json);
        var t = MmfScraperPlugin.ResolveDownloadTargets(doc.RootElement, "X");
        Assert.Equal(3, t.Count);
        Assert.Equal(new[] { "Part_A.zip", "Part_B.zip", "Part_C.zip" }, t.Select(x => x.FileName));
        Assert.All(t, x => Assert.StartsWith("https://www.myminifactory.com/download/802018?archive_id=", x.Url));
    }

    [Theory]
    [InlineData("{\"archive_download_url\":null}")]
    [InlineData("{\"archive_download_url\":\"\",\"files\":{\"total_count\":0,\"items\":[]}}")]
    [InlineData("{\"files\":{\"items\":[{\"filename\":\"a.zip\",\"download_url\":null}]}}")]
    public void NoLinks_ReturnsEmpty(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Empty(MmfScraperPlugin.ResolveDownloadTargets(doc.RootElement, "X"));
    }

    [Fact]
    public void DuplicateFilenames_AreDisambiguated()
    {
        using var doc = JsonDocument.Parse("{\"files\":{\"items\":[{\"filename\":\"a.zip\",\"download_url\":\"/download/1?archive_id=1\"},{\"filename\":\"a.zip\",\"download_url\":\"/download/1?archive_id=2\"}]}}");
        var t = MmfScraperPlugin.ResolveDownloadTargets(doc.RootElement, "X");
        Assert.Equal(new[] { "a.zip", "a-2.zip" }, t.Select(x => x.FileName));
    }
}

public class MmfHostFixesTests
{
    [Theory]
    [InlineData(false, false, PluginHostService.ManifestSource.Live)]
    [InlineData(true, false, PluginHostService.ManifestSource.Live)]
    [InlineData(false, true, PluginHostService.ManifestSource.Uploaded)]
    [InlineData(true, true, PluginHostService.ManifestSource.UploadedOffline)]
    public void UploadedManifest_TakesPriority(bool dryRun, bool hasManifest, PluginHostService.ManifestSource expected) =>
        Assert.Equal(expected, PluginHostService.ChooseManifestSource(dryRun, hasManifest));

    [Fact]
    public void EffectiveSchema_DeclaresDryRun_WithoutDuplicatingPluginFields()
    {
        var plugin = new[] { new PluginConfigField { Key = "REHOME_UNKNOWN_APPLY", Label = "x", Type = PluginConfigFieldType.String } };
        var eff = PluginHostService.EffectiveConfigSchema(plugin);
        Assert.Contains(eff, f => f.Key == "DRY_RUN");
        Assert.Single(eff, f => f.Key == "REHOME_UNKNOWN_APPLY");
        Assert.Same(plugin[0], eff.First(f => f.Key == "REHOME_UNKNOWN_APPLY"));
    }

    [Fact]
    public void FailedItem_RemovesOnlyFoldersCreatedThisRun_AndOnlyIfEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "fk-dir-" + Guid.NewGuid().ToString("N"));
        var existingCreator = Path.Combine(root, "creator");
        Directory.CreateDirectory(existingCreator);
        try
        {
            var model = Path.Combine(existingCreator, "model");
            var created = PluginHostService.CreateDirectoryTracked(model);
            Assert.Single(created);
            PluginHostService.RemoveEmptyCreatedDirs(created);
            Assert.False(Directory.Exists(model));
            Assert.True(Directory.Exists(existingCreator)); // pre-existing: untouched

            var nested = Path.Combine(root, "newcreator", "m2");
            var c2 = PluginHostService.CreateDirectoryTracked(nested);
            Assert.Equal(2, c2.Count);
            PluginHostService.RemoveEmptyCreatedDirs(c2);
            Assert.False(Directory.Exists(Path.Combine(root, "newcreator")));

            var withFile = Path.Combine(root, "c3", "m3");
            var c3 = PluginHostService.CreateDirectoryTracked(withFile);
            File.WriteAllText(Path.Combine(withFile, "partial.txt"), "x");
            PluginHostService.RemoveEmptyCreatedDirs(c3);
            Assert.True(Directory.Exists(withFile)); // non-empty: kept

            var preexisting = PluginHostService.CreateDirectoryTracked(existingCreator);
            Assert.Empty(preexisting);
        }
        finally { Directory.Delete(root, true); }
    }
}
