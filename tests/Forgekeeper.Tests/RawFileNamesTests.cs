using System.Runtime.InteropServices;
using System.Text;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Forgekeeper.Core.Interfaces;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>#41 / #58: file names that aren't valid UTF-8.</summary>
public class RawFileNamesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fk-raw-" + Guid.NewGuid().ToString("N"));

    public RawFileNamesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [DllImport("libc", SetLastError = true)] private static extern int creat(byte[] path, int mode);
    [DllImport("libc")] private static extern int close(int fd);

    private void CreateRaw(string dir, byte[] name)
    {
        var path = Encoding.UTF8.GetBytes(dir + "/").Concat(name).Append((byte)0).ToArray();
        var fd = creat(path, Convert.ToInt32("644", 8));
        Assert.True(fd >= 0, "creat failed");
        close(fd);
    }

    private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    [Theory]
    [InlineData(new byte[] { 0x53, 0xE9, 0x63 }, "Séc")]           // Latin-1 é
    [InlineData(new byte[] { 0x41, 0xB0, 0x2E, 0x53 }, "A°.S")]   // P012 0xB0
    [InlineData(new byte[] { 0x93, 0x78, 0x94 }, "“x”")]          // CP1252 quotes
    [InlineData(new byte[] { 0xC3, 0xA9, 0xE9 }, "éé")]           // valid UTF-8 kept, bad byte mapped
    public void RepairName_DecodesInvalidBytesAsCp1252(byte[] raw, string expected)
    {
        Assert.False(RawFileNames.IsValidUtf8(raw));
        Assert.Equal(expected, RawFileNames.RepairName(raw));
    }

    [Fact]
    public void RepairName_ValidUtf8_Unchanged()
    {
        var raw = Encoding.UTF8.GetBytes("Séchoir 1 - 28mm.stl");
        Assert.True(RawFileNames.IsValidUtf8(raw));
        Assert.Equal("Séchoir 1 - 28mm.stl", RawFileNames.RepairName(raw));
    }

    [Fact]
    public void RepairDirectory_RenamesLossyFile_AndItBecomesReadable()
    {
        if (!OperatingSystem.IsLinux()) return;
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "sup")).FullName;
        CreateRaw(sub, Latin1("P012_HEAVY_EN(SUPPORTED\u00B0.STL"));
        CreateRaw(_dir, Latin1("S\u00E9choir 1.stl"));

        var lossy = Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).ToList();
        Assert.All(lossy, f => Assert.True(RawFileNames.LooksLossy(f)));
        Assert.Throws<FileNotFoundException>(() => new FileInfo(lossy[0]).Length);

        var dry = RawFileNames.RepairDirectory(_dir, recursive: true, dryRun: true);
        Assert.Equal(2, dry.Count);
        Assert.All(Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories), f => Assert.True(RawFileNames.LooksLossy(f)));

        var renamed = RawFileNames.RepairDirectory(_dir, recursive: true);
        Assert.Equal(2, renamed.Count);
        Assert.True(File.Exists(Path.Combine(sub, "P012_HEAVY_EN(SUPPORTED°.STL")));
        Assert.True(File.Exists(Path.Combine(_dir, "Séchoir 1.stl")));
        Assert.Equal(0, new FileInfo(Path.Combine(_dir, "Séchoir 1.stl")).Length);
    }

    [Fact]
    public void RepairDirectory_NeverOverwritesExistingTarget()
    {
        if (!OperatingSystem.IsLinux()) return;
        File.WriteAllText(Path.Combine(_dir, "Séchoir.stl"), "keep");
        CreateRaw(_dir, Latin1("S\u00E9choir.stl"));

        var renamed = RawFileNames.RepairDirectory(_dir, recursive: false);
        Assert.Single(renamed);
        Assert.EndsWith("Séchoir (1).stl", renamed[0].NewPath);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_dir, "Séchoir.stl")));
    }

    private FileScannerService Scanner(bool repair) => new(
        Mock.Of<IDbContextFactory<ForgeDbContext>>(), Array.Empty<ISourceAdapter>(), Mock.Of<IMetadataService>(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Scanner:RepairNonUtf8Names"] = repair ? "true" : "false",
        }).Build(),
        NullLogger<FileScannerService>.Instance);

    private List<(string, string, string)> Enumerate() =>
        Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)
            .Select(f => (f, Path.GetRelativePath(_dir, f), Path.GetFileName(f))).ToList();

    [Fact]
    public void Scanner_RepairEnabled_RenamesAndReturnsReadableFiles()
    {
        if (!OperatingSystem.IsLinux()) return;
        CreateRaw(_dir, Latin1("S\u00E9choir.stl"));
        var files = Scanner(repair: true).RepairNonUtf8Names(_dir, Enumerate());
        var f = Assert.Single(files);
        Assert.Equal("Séchoir.stl", f.FileName);
        Assert.True(File.Exists(f.FullPath));
    }

    [Fact]
    public void Scanner_RepairDisabled_LeavesFilesAlone()
    {
        if (!OperatingSystem.IsLinux()) return;
        CreateRaw(_dir, Latin1("S\u00E9choir.stl"));
        var files = Scanner(repair: false).RepairNonUtf8Names(_dir, Enumerate());
        Assert.True(RawFileNames.LooksLossy(Assert.Single(files).FileName));
    }
}
