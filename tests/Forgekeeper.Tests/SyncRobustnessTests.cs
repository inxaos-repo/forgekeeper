using Forgekeeper.Infrastructure.Services;
using Forgekeeper.PluginSdk;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>#75 / #77 partial-download handling and #28 hollow-success detection.</summary>
public class SyncRobustnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fk-robust-{Guid.NewGuid():N}");
    public SyncRobustnessTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Model(string creator, string model, params string[] files)
    {
        var d = Path.Combine(_dir, creator, model);
        Directory.CreateDirectory(d);
        foreach (var f in files) File.WriteAllText(Path.Combine(d, f), "x");
        return d;
    }

    [Fact]
    public void StubDetection()
    {
        Assert.True(PartialDownloadCleanup.IsStubOrPartialDir(Model("c", "a")));
        Assert.True(PartialDownloadCleanup.IsStubOrPartialDir(Model("c", "b", "metadata.json")));
        Assert.True(PartialDownloadCleanup.IsStubOrPartialDir(Model("c", "p", ".m.zip.1234.part", "metadata.json")));
        Assert.False(PartialDownloadCleanup.IsStubOrPartialDir(Model("c", "z", "m.zip", "metadata.json")));
        Assert.False(PartialDownloadCleanup.IsStubOrPartialDir(Model("c", "s", "m.stl")));
        Assert.False(PartialDownloadCleanup.IsStubOrPartialDir(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void SweepMovesOnlyStalePartsToTrash()
    {
        var oldDir = Model("c", "old", ".a.zip.1.part", "metadata.json");
        var newDir = Model("c", "fresh", ".b.zip.2.part");
        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(Path.Combine(oldDir, ".a.zip.1.part"), now.AddHours(-2));

        var moved = PartialDownloadCleanup.SweepOrphanParts(_dir, TimeSpan.FromMinutes(30), nowUtc: now);

        Assert.Equal(1, moved);
        Assert.False(File.Exists(Path.Combine(oldDir, ".a.zip.1.part")));
        Assert.True(File.Exists(Path.Combine(_dir, $".trash-{now:yyyyMMdd}", "c", "old", ".a.zip.1.part")));
        Assert.True(File.Exists(Path.Combine(oldDir, "metadata.json")));
        Assert.True(File.Exists(Path.Combine(newDir, ".b.zip.2.part"))); // possibly in flight: untouched
    }

    [Fact]
    public void HollowSuccessIsDetected()
    {
        var empty = Model("c", "e", "metadata.json");
        var full = Model("c", "f", "m.stl");
        var ok = ScrapeResult.Ok("metadata.json", []);
        Assert.True(PluginHostService.IsHollowSuccess(ok, empty));
        Assert.False(PluginHostService.IsHollowSuccess(ok, full)); // files already on disk = fine
        Assert.False(PluginHostService.IsHollowSuccess(ScrapeResult.Failure("x"), empty));
    }

    [Fact]
    public void HollowRunWarning()
    {
        Assert.NotNull(PluginHostService.HollowRunWarning(10, 0, 0));
        Assert.Null(PluginHostService.HollowRunWarning(10, 3, 100));
        Assert.Null(PluginHostService.HollowRunWarning(2, 0, 0));
    }
}
