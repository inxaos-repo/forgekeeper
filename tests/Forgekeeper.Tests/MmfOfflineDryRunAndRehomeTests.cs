using System.Text.Json;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Services;
using Forgekeeper.PluginSdk;
using Forgekeeper.Scraper.Mmf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>Issue #33: offline DRY_RUN from an uploaded manifest + separate re-home apply.</summary>
public class MmfOfflineDryRunAndRehomeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _src; // sources/mmf

    public MmfOfflineDryRunAndRehomeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fk-i33-{Guid.NewGuid():N}");
        _src = Path.Combine(_tempDir, "sources", "mmf");
        Directory.CreateDirectory(_src);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    private static string FixtureJson() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mmf", "objectPreviews.sample.json"));

    private (PluginHostService Host, IServiceProvider Sp) CreateHost()
    {
        var services = new ServiceCollection();
        var dbName = $"i33_{Guid.NewGuid()}";
        services.AddSingleton<IDbContextFactory<ForgeDbContext>>(new InMemFactory(dbName));
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forgekeeper:PluginsDirectory"] = Path.Combine(_tempDir, "plugins"),
            ["Forgekeeper:SourcesDirectory"] = Path.Combine(_tempDir, "sources"),
        }).Build();
        var host = new PluginHostService(sp, new Mock<ILogger<PluginHostService>>().Object, config,
            new ManifestValidationService(new Mock<ILogger<ManifestValidationService>>().Object),
            new SdkCompatibilityChecker(new Mock<ILogger<SdkCompatibilityChecker>>().Object));
        return (host, sp);
    }

    /// <summary>Scraper that parses an uploaded stream but fails loudly on any network/auth path.</summary>
    private static Mock<ILibraryScraper> OfflineOnlyScraper()
    {
        var m = new Mock<ILibraryScraper>(MockBehavior.Loose);
        m.SetupGet(s => s.SourceSlug).Returns("mmf");
        m.SetupGet(s => s.SourceName).Returns("MyMiniFactory");
        m.SetupGet(s => s.ConfigSchema).Returns(Array.Empty<PluginConfigField>());
        m.Setup(s => s.AuthenticateAsync(It.IsAny<PluginContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("auth must not be called in offline dry run"));
        m.Setup(s => s.FetchManifestAsync(It.IsAny<PluginContext>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("live fetch must not be called in offline dry run"));
        m.Setup(s => s.FetchManifestAsync(It.IsAny<PluginContext>(), It.Is<Stream?>(x => x != null), It.IsAny<CancellationToken>()))
            .Returns(async (PluginContext _, Stream? st, CancellationToken ct) =>
            {
                using var r = new StreamReader(st!);
                return MmfManifestParser.Parse(await r.ReadToEndAsync(ct)).Models;
            });
        return m;
    }

    [Fact]
    public void ManifestStore_SavesAndReturnsLatest()
    {
        Assert.Null(UploadedManifestStore.GetLatest(_src, "mmf"));
        var bytes = System.Text.Encoding.UTF8.GetBytes(FixtureJson());
        var a = UploadedManifestStore.Save(_src, "mmf", bytes, 11, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(a.Path, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var b = UploadedManifestStore.Save(_src, "mmf", bytes, 11, new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc));

        var latest = UploadedManifestStore.GetLatest(_src, "mmf");
        Assert.NotNull(latest);
        Assert.Equal(b.Path, latest!.Path);
        Assert.Equal(11, latest.RowCount);
        Assert.Equal(64, latest.Sha256.Length);
        Assert.True(File.Exists(a.Path));
    }

    [Fact]
    public async Task DryRun_UsesStoredManifest_WithoutAuthOrLiveFetch()
    {
        var (host, sp) = CreateHost();
        var scraper = OfflineOnlyScraper();
        host.RegisterPluginForTest("mmf", scraper.Object);

        var dbf = sp.GetRequiredService<IDbContextFactory<ForgeDbContext>>();
        await using (var db = await dbf.CreateDbContextAsync())
        {
            db.PluginConfigs.Add(new PluginConfig { PluginSlug = "mmf", Key = "DRY_RUN", Value = "true" });
            await db.SaveChangesAsync();
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(FixtureJson());
        UploadedManifestStore.Save(_src, "mmf", bytes, 11);

        await host.RunSyncAsync("mmf", CancellationToken.None);

        var status = host.GetSyncStatus("mmf");
        Assert.NotNull(status);
        Assert.Null(status!.Error);
        Assert.NotNull(status.LastReportPath);
        Assert.True(File.Exists(status.LastReportPath));
        scraper.Verify(s => s.AuthenticateAsync(It.IsAny<PluginContext>(), It.IsAny<CancellationToken>()), Times.Never);
        scraper.Verify(s => s.FetchManifestAsync(It.IsAny<PluginContext>(), null, It.IsAny<CancellationToken>()), Times.Never);
        scraper.Verify(s => s.ScrapeModelAsync(It.IsAny<PluginContext>(), It.IsAny<ScrapedModel>(), It.IsAny<CancellationToken>()), Times.Never);

        var reports = Path.Combine(_src, ".forgekeeper-reports");
        Assert.Single(Directory.GetFiles(reports, "dryrun-mmf-*.json"));
        Assert.Single(Directory.GetFiles(reports, "rehome-unknown-mmf-*.json"));
        await using (var db = await dbf.CreateDbContextAsync())
            Assert.Equal("dry-run", (await db.SyncRuns.SingleAsync()).Status);
    }

    // --- re-home apply ---------------------------------------------------------------

    private string MakeUnknown(string name)
    {
        var d = Path.Combine(_src, "unknown", name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "model.stl"), name);
        return d;
    }

    private string WriteReport(string name, params object[] items)
    {
        var dir = Path.Combine(_src, ".forgekeeper-reports");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), JsonSerializer.Serialize(new { items }));
        return name;
    }

    private static object Item(string folder, string target, string status = "Movable") =>
        new { folder, externalId = "1", creatorName = "c", targetPath = target, status };

    [Fact]
    public void Apply_MovesAndIsIdempotent_AndWritesLog()
    {
        var a = MakeUnknown("A");
        var target = Path.Combine(_src, "Creator", "A");
        var report = WriteReport("rehome-unknown-mmf-1.json", Item(a, target));

        var r1 = RehomeApplier.Apply(_src, "mmf", report);
        Assert.Equal(1, r1.Moved);
        Assert.False(Directory.Exists(a));
        Assert.True(File.Exists(Path.Combine(target, "model.stl")));
        Assert.True(File.Exists(r1.LogPath));

        var r2 = RehomeApplier.Apply(_src, "mmf", report);
        Assert.Equal(0, r2.Moved);
        Assert.Equal(RehomeApplyOutcome.AlreadyApplied, Assert.Single(r2.Items).Outcome);
        Assert.NotEqual(r1.LogPath, r2.LogPath);
        Assert.True(File.Exists(Path.Combine(target, "model.stl")));
    }

    [Fact]
    public void Apply_SkipsExistingTarget_NeverOverwritesOrMerges()
    {
        var b = MakeUnknown("B");
        var target = Path.Combine(_src, "Creator", "B");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "original");
        var report = WriteReport("rehome-unknown-mmf-2.json", Item(b, target));

        var r = RehomeApplier.Apply(_src, "mmf", report);
        Assert.Equal(RehomeApplyOutcome.SkippedTargetExists, Assert.Single(r.Items).Outcome);
        Assert.True(File.Exists(Path.Combine(b, "model.stl")));          // source untouched
        Assert.False(File.Exists(Path.Combine(target, "model.stl")));    // no merge
        Assert.Equal("original", File.ReadAllText(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public void Apply_RefusesPathsOutsideSourceDir()
    {
        var c = MakeUnknown("C");
        var outside = Path.Combine(_tempDir, "elsewhere", "C");
        var otherSource = Path.Combine(_tempDir, "sources", "thangs", "X", "C");
        var notInUnknown = Path.Combine(_src, "Someone", "D");
        Directory.CreateDirectory(notInUnknown);
        var report = WriteReport("rehome-unknown-mmf-3.json",
            Item(c, outside),
            Item(c, otherSource),
            Item(c, Path.Combine(_src, "Creator", "..", "..", "thangs", "C")),
            Item(c, Path.Combine(_src, "unknown", "Other", "C")),
            Item(c, Path.Combine(_src, "Creator", "nested", "C")),
            Item(notInUnknown, Path.Combine(_src, "Creator", "D")),
            Item(Path.Combine(_src, "unknown", "..", "..", "thangs"), Path.Combine(_src, "Creator", "T")));

        var r = RehomeApplier.Apply(_src, "mmf", report);
        Assert.All(r.Items, i => Assert.Equal(RehomeApplyOutcome.RefusedOutsideSource, i.Outcome));
        Assert.True(Directory.Exists(c));
        Assert.True(Directory.Exists(notInUnknown));
        Assert.False(Directory.Exists(outside));
        Assert.False(Directory.Exists(otherSource));
    }

    [Fact]
    public void Apply_IgnoresNonMovableEntries()
    {
        var e = MakeUnknown("E");
        var report = WriteReport("rehome-unknown-mmf-4.json", Item(e, Path.Combine(_src, "Creator", "E"), "TargetExists"));
        var r = RehomeApplier.Apply(_src, "mmf", report);
        Assert.Equal(RehomeApplyOutcome.NotMovable, Assert.Single(r.Items).Outcome);
        Assert.True(Directory.Exists(e));
    }

    [Theory]
    [InlineData("../rehome-unknown-mmf-1.json")]
    [InlineData("/etc/passwd")]
    [InlineData("dryrun-mmf-1.json")]
    [InlineData("rehome-unknown-applied-mmf-1.json")]
    public void Apply_RejectsBadReportNames(string name)
    {
        Assert.Throws<ArgumentException>(() => RehomeApplier.Apply(_src, "mmf", name));
    }

    [Fact]
    public async Task PlanThenApply_ViaHost_WorksOfflineWithoutDryRunFlag()
    {
        var (host, _) = CreateHost();
        var scraper = OfflineOnlyScraper();
        host.RegisterPluginForTest("mmf", scraper.Object);
        var json = FixtureJson();
        var models = MmfManifestParser.Parse(json).Models;
        var m = models.First(x => !string.IsNullOrWhiteSpace(x.CreatorName));
        UploadedManifestStore.Save(_src, "mmf", System.Text.Encoding.UTF8.GetBytes(json), models.Count);

        var folder = MakeUnknown("Thing");
        File.WriteAllText(Path.Combine(folder, "metadata.json"), JsonSerializer.Serialize(new { externalId = m.ExternalId }));

        var plan = await host.PlanRehomeAsync("mmf", CancellationToken.None);
        Assert.Equal(1, plan.Movable);
        Assert.True(Directory.Exists(folder)); // plan moves nothing

        var applied = await host.ApplyRehomeReportAsync("mmf", Path.GetFileName(plan.Json), CancellationToken.None);
        Assert.Equal(1, applied.Moved);
        Assert.False(Directory.Exists(folder));
        Assert.False(host.GetSyncStatus("mmf")!.IsRunning);
        scraper.Verify(s => s.AuthenticateAsync(It.IsAny<PluginContext>(), It.IsAny<CancellationToken>()), Times.Never);

        var again = await host.ApplyRehomeReportAsync("mmf", Path.GetFileName(plan.Json), CancellationToken.None);
        Assert.Equal(0, again.Moved);
    }

    private sealed class InMemFactory(string name) : IDbContextFactory<ForgeDbContext>
    {
        public ForgeDbContext CreateDbContext() => TestDbContextFactory.Create(name);
    }
}
