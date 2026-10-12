using Forgekeeper.Infrastructure.Services;
using Forgekeeper.PluginSdk;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Info-only folders (metadata.json sidecar only, or empty) must not count as "already in library"
/// for NEW_ONLY; they are selected for an in-place repair into the existing folder.
/// </summary>
public class InfoOnlyRepairTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fk-infoonly-" + Guid.NewGuid().ToString("N"));

    public InfoOnlyRepairTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Folder(string creator, string name, string? externalId, params string[] files)
    {
        var dir = Path.Combine(_root, creator, name);
        Directory.CreateDirectory(dir);
        if (externalId != null)
            File.WriteAllText(Path.Combine(dir, "metadata.json"),
                $$"""{"source":"mmf","externalId":"{{externalId}}","files":[{"filename":"a.zip","size":1000},{"filename":"b.stl","size":234}]}""");
        foreach (var f in files)
        {
            var p = Path.Combine(dir, f);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "x");
        }
        return dir;
    }

    private static ScrapedModel Model(string id, string creator, string name) =>
        new() { ExternalId = id, Name = name, CreatorName = creator };

    private DownloadSelection Select(string id, string creator, string name)
    {
        var index = new SourceIdIndex();
        index.ScanMetadata(_root);
        return PluginHostService.SelectForDownload(Model(id, creator, name), index, _root);
    }

    [Fact]
    public void HasContentFiles_DetectsModelFilesRecursively_IgnoresSidecarPreviewsAndHidden()
    {
        Assert.False(ModelFolderContent.HasContentFiles(Folder("C", "sidecar", "object-1")));
        Assert.False(ModelFolderContent.HasContentFiles(Folder("C", "empty", null)));
        Assert.False(ModelFolderContent.HasContentFiles(Folder("C", "previews", "object-2", "cover.jpg", "readme.txt")));
        Assert.False(ModelFolderContent.HasContentFiles(Folder("C", "partial", "object-3", ".a.zip.123.part", ".hidden/x.stl")));
        Assert.True(ModelFolderContent.HasContentFiles(Folder("C", "stl", "object-4", "sub/dir/Model.STL")));
        Assert.True(ModelFolderContent.HasContentFiles(Folder("C", "zip", "object-5", "pack.zip")));
        Assert.True(ModelFolderContent.HasContentFiles(Folder("C", "lys", "object-6", "supported.lys")));
        Assert.False(ModelFolderContent.HasContentFiles(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void SidecarOnlyFolder_ById_IsSelectedForInPlaceRepair()
    {
        Folder("Creator", "Some Model", "object-100");
        Assert.Equal(DownloadSelection.RepairInfoOnly, Select("object-100", "Creator", "Some Model"));
    }

    [Fact]
    public void EmptyFolder_ByName_IsSelectedForInPlaceRepair()
    {
        Folder("Creator", "Named Model", null);
        Assert.Equal(DownloadSelection.RepairInfoOnly, Select("object-200", "Creator", "Named Model"));
    }

    [Fact]
    public void FolderWithContent_IsSkipped_AndUnknownItemIsNew()
    {
        Folder("Creator", "Done Model", "object-300", "files/done.stl");
        Assert.Equal(DownloadSelection.Skip, Select("object-300", "Creator", "Done Model"));
        Assert.Equal(DownloadSelection.New, Select("object-999", "Creator", "Brand New"));
    }

    [Fact]
    public void ConflictingFolders_AreNeverSelected_EvenWhenEmpty()
    {
        Folder("Creator", "Goblin A", "object-450");
        Folder("Creator", "Goblin B", "450");
        Assert.Equal(DownloadSelection.Skip, Select("object-450", "Creator", "Goblin"));
    }

    [Fact]
    public void Repair_TargetsTheExistingFolder_NotANewOne()
    {
        // Existing folder name differs from the manifest name; the id match must win so the
        // download fills the existing folder (and the scanner reuses the existing model row).
        var dir = Folder("Creator", "Old Folder Name", "object-500");
        var index = new SourceIdIndex();
        index.ScanMetadata(_root);
        var entry = LibraryReconciler.Classify(Model("object-500", "Creator", "Renamed Upstream"), index, _root,
            (c, n) => null, s => s);
        Assert.Equal(DownloadSelection.RepairInfoOnly, LibraryReconciler.SelectForDownload(entry));
        Assert.Equal(dir, entry.ExistingPath);
        Assert.False(Directory.Exists(Path.Combine(_root, "Creator", "Renamed Upstream")));
    }

    [Fact]
    public void UnreadableFolder_IsTreatedAsHavingContent()
    {
        var entry = new ReconcileEntry("object-1", "n", "c", ReconcileStatus.OnDiskById, _root, _root, null, null);
        Assert.Equal(DownloadSelection.Skip, LibraryReconciler.SelectForDownload(entry, _ => true));
        Assert.Equal(DownloadSelection.RepairInfoOnly, LibraryReconciler.SelectForDownload(entry, _ => false));
    }

    [Fact]
    public void FindInfoOnly_ListsSidecarAndEmptyFolders_WithExpectedBytes()
    {
        Folder("A", "info", "object-1");
        Folder("A", "full", "object-2", "m.stl");
        Folder("B", "empty", null);
        Directory.CreateDirectory(Path.Combine(_root, ".forgekeeper-reports", "x"));

        var list = ModelFolderContent.FindInfoOnly(_root);
        Assert.Equal(2, list.Count);
        var info = Assert.Single(list, f => f.Folder == "info");
        Assert.True(info.HasMetadata);
        Assert.Equal("object-1", info.ExternalId);
        Assert.Equal(1234, info.ExpectedBytes);
        var empty = Assert.Single(list, f => f.Folder == "empty");
        Assert.False(empty.HasMetadata);
        Assert.Null(empty.ExpectedBytes);
    }
}
