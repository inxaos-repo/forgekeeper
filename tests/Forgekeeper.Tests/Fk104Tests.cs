using Forgekeeper.Infrastructure.Services;
using Forgekeeper.PluginSdk;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// #104: one sanitizer for host + plugin, no empty siblings next to legacy-named folders,
/// and stale DB paths (after a folder reorg) resolved instead of downloading to mmf/unknown.
/// </summary>
public class Fk104Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fk-104-" + Guid.NewGuid().ToString("N"));

    public Fk104Tests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Folder(string creator, string name, string? externalId, params string[] files)
    {
        var dir = Path.Combine(_root, creator, name);
        Directory.CreateDirectory(dir);
        if (externalId != null)
            File.WriteAllText(Path.Combine(dir, "metadata.json"), $$"""{"source":"mmf","externalId":"{{externalId}}"}""");
        foreach (var f in files) File.WriteAllText(Path.Combine(dir, f), "x");
        return dir;
    }

    [Theory]
    [InlineData("Human Artificer/Wizard", "Human Artificer_Wizard")]
    [InlineData("  padded  ", "padded")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    [InlineData("..", "unknown")]
    public void Sanitize_IsStableAndHandlesEdgeCases(string? input, string expected) =>
        Assert.Equal(expected, PathNames.Sanitize(input));

    [Fact]
    public void Sanitize_HostAndPluginAgree()
    {
        foreach (var n in new[] { "A/B", "Hive Secundus : \tIron Maw", "x\0y", " t " })
            Assert.Equal(PathNames.Sanitize(n), Forgekeeper.Scraper.Mmf.MmfScraperPlugin.SanitizeFilename(n));
    }

    [Fact]
    public void ComparisonKey_IgnoresSeparatorVariants()
    {
        Assert.Equal(PathNames.ComparisonKey("Shuttle 1:270 and X"), PathNames.ComparisonKey("Shuttle 1-270 and X"));
        Assert.Equal(PathNames.ComparisonKey("Artificer_Wizard"), PathNames.ComparisonKey("Artificer-Wizard"));
        Assert.NotEqual(PathNames.ComparisonKey("Book 1"), PathNames.ComparisonKey("Book 2"));
    }

    [Fact]
    public void FindEquivalent_PrefersLegacySiblingWithContent_OverEmptyExactStub()
    {
        var real = Folder("Artificers_Mini", "Cinder Sparks - Human Artificer-Wizard", "object-1", "a.stl");
        Folder("Artificers_Mini", "Cinder Sparks - Human Artificer_Wizard", "object-1"); // stub
        var found = PathNames.FindEquivalentDirectory(Path.Combine(_root, "Artificers_Mini"),
            "Cinder Sparks - Human Artificer_Wizard", ModelFolderContent.HasContentFiles);
        Assert.Equal(real, found);
    }

    [Fact]
    public void FindEquivalent_ReturnsLegacySibling_SoNoNewFolderIsCreated()
    {
        var real = Folder("C", "Fir Trees -Modular Set-", null, "a.stl");
        var found = PathNames.FindEquivalentDirectory(Path.Combine(_root, "C"), "Fir Trees _Modular Set_", ModelFolderContent.HasContentFiles);
        Assert.Equal(real, found);
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "C")));
    }

    [Fact]
    public void FindEquivalent_NullWhenNothingMatches()
    {
        Folder("C", "Other", null, "a.stl");
        Assert.Null(PathNames.FindEquivalentDirectory(Path.Combine(_root, "C"), "Brand New", ModelFolderContent.HasContentFiles));
    }

    [Fact]
    public void ResolveExisting_DropsStaleDbPath_FallsBackToMetadata()
    {
        var real = Folder("PopovLaboratory", "Iron Maw", "object-597143", "a.stl");
        var index = new SourceIdIndex();
        index.AddFromDb("object-597143", Path.Combine(_root, "OldCreator", "Iron Maw")); // gone after reorg
        index.ScanMetadata(_root);
        Assert.Equal(new[] { real }, index.ResolveExisting("597143"));
    }

    [Fact]
    public void ResolveExisting_PrefersFolderWithContent_OverStub()
    {
        var real = Folder("C", "M-1", "object-9", "a.stl");
        Folder("C", "M_1", "object-9");
        var index = new SourceIdIndex();
        index.ScanMetadata(_root);
        Assert.Equal(new[] { real }, index.ResolveExisting("object-9"));
    }

    [Fact]
    public void Classify_StaleDbPath_IsOnDiskInCreatorFolder_NotNew()
    {
        var real = Folder("PopovLaboratory", "Iron Maw", "object-597143", "a.stl");
        var index = new SourceIdIndex();
        index.AddFromDb("object-597143", Path.Combine(_root, "Old", "Iron Maw"));
        index.ScanMetadata(_root);
        var entry = LibraryReconciler.Classify(
            new ScrapedModel { ExternalId = "object-597143", Name = "Iron Maw", CreatorName = "PopovLaboratory" },
            index, _root, (_, _) => null, s => PathNames.Sanitize(s));
        Assert.Equal(ReconcileStatus.OnDiskById, entry.Status);
        Assert.Equal(real, entry.ExistingPath);
        Assert.Equal(DownloadSelection.Skip, LibraryReconciler.SelectForDownload(entry));
    }

    [Fact]
    public void ResolveStalePath_FindsSingleUnownedMetadataFolder()
    {
        var real = Folder("PopovLaboratory", "Iron Maw", "object-597143", "a.stl");
        var index = new SourceIdIndex();
        index.ScanMetadata(_root);
        Assert.Equal(real, FileScannerService.ResolveStalePath("object-597143", index, new HashSet<string>()));
        // Already owned by another row → don't steal it (unique base_path)
        Assert.Null(FileScannerService.ResolveStalePath("object-597143", index, new HashSet<string> { real }));
        Assert.Null(FileScannerService.ResolveStalePath(null, index, new HashSet<string>()));
        Assert.Null(FileScannerService.ResolveStalePath("object-404", index, new HashSet<string>()));
    }
}
