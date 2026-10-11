using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>#73: live metadata-only twins, scan/sync race gate, background job, idempotent re-runs.</summary>
[Collection("LibraryMaintenanceGate")] // the gate is process-wide
public class MmfOrphanMergeLiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fk-merge73-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static MetadataService Meta() => new(NullLogger<MetadataService>.Instance);

    private string Dir(string rel)
    {
        var d = Path.Combine(_root, rel);
        Directory.CreateDirectory(d);
        return d;
    }

    private async Task<(Creator C, Model3D Orphan, Model3D Keep)> SeedLivePair(ForgeDbContext db, bool orphanHasContent = false)
    {
        var meta = Meta();
        var c = new Creator { Id = Guid.NewGuid(), Name = "Creator", Source = SourceType.Mmf };
        db.Creators.Add(c);
        var orphanDir = Dir("sources/mmf/Creator/Tower (mmf)");
        await meta.WriteAsync(orphanDir, new SourceMetadata
        {
            MetadataVersion = 1, Source = "mmf", ExternalId = "object-42", Name = "Tower",
            ExternalUrl = "https://www.myminifactory.com/object/42", Description = "Tall", Tags = ["tower"],
        });
        if (orphanHasContent) await File.WriteAllTextAsync(Path.Combine(orphanDir, "preview.png"), "x");
        var keepDir = Dir("sources/mmf/Creator/Tower");
        await File.WriteAllTextAsync(Path.Combine(keepDir, "tower.stl"), "solid");
        var orphan = new Model3D
        {
            Id = Guid.NewGuid(), CreatorId = c.Id, Name = "Tower", Source = SourceType.Mmf, SourceId = "object-42",
            SourceUrl = "https://www.myminifactory.com/object/42", Description = "Tall", BasePath = orphanDir, FileCount = 0,
        };
        var keep = new Model3D { Id = Guid.NewGuid(), CreatorId = c.Id, Name = "Tower", BasePath = keepDir, FileCount = 1 };
        db.Models.AddRange(orphan, keep);
        await db.SaveChangesAsync();
        return (c, orphan, keep);
    }

    [Fact]
    public async Task LiveTwin_IgnoredByDefault_MergedWithIncludeLive_FolderKept()
    {
        using var db = TestDbContextFactory.Create();
        var (_, orphan, keep) = await SeedLivePair(db);
        var svc = new MmfOrphanMergeService(db, Meta(), NullLogger<MmfOrphanMergeService>.Instance);

        var def = await svc.MergeOrphansAsync(apply: true);
        Assert.Equal(0, def.Pairs);
        Assert.Equal(2, await db.Models.CountAsync());

        var dry = await svc.MergeOrphansAsync(apply: false, includeLive: true);
        Assert.Equal(1, dry.LiveOrphans);
        Assert.Equal(1, dry.Pairs);
        Assert.True(dry.PairList[0].Live);
        Assert.Single(dry.OrphanBackup);
        Assert.Equal(orphan.Id, dry.OrphanBackup[0]["Id"]);

        var r = await svc.MergeOrphansAsync(apply: true, includeLive: true);
        Assert.Equal(1, r.Merged);
        var k = Assert.Single(await db.Models.ToListAsync());
        Assert.Equal(keep.Id, k.Id);
        Assert.Equal("object-42", k.SourceId);
        Assert.True(Directory.Exists(orphan.BasePath)); // never deleted or moved
        Assert.True(PartialDownloadCleanup.IsStubOrPartialDir(orphan.BasePath)); // scanner (#80) won't recreate a row

        // Idempotent: a re-run finds nothing more to do.
        var again = await svc.MergeOrphansAsync(apply: true, includeLive: true);
        Assert.Equal(0, again.Pairs);
        Assert.Equal(0, again.Merged);
        Assert.Single(await db.Models.ToListAsync());
    }

    [Fact]
    public async Task LiveTwin_WithContentFiles_IsSkipped()
    {
        using var db = TestDbContextFactory.Create();
        await SeedLivePair(db, orphanHasContent: true);
        var svc = new MmfOrphanMergeService(db, Meta(), NullLogger<MmfOrphanMergeService>.Instance);
        var r = await svc.MergeOrphansAsync(apply: true, includeLive: true);
        Assert.Equal(0, r.Merged);
        Assert.Single(r.SkippedList);
        Assert.Equal(2, await db.Models.CountAsync());
    }

    [Fact]
    public async Task LiveTwin_MissingFolder_IsMergeable_SoPartialRunsCanResume()
    {
        using var db = TestDbContextFactory.Create();
        var (_, orphan, _) = await SeedLivePair(db);
        Directory.Delete(orphan.BasePath, true);
        var svc = new MmfOrphanMergeService(db, Meta(), NullLogger<MmfOrphanMergeService>.Instance);
        var r = await svc.MergeOrphansAsync(apply: true, includeLive: true);
        Assert.Equal(1, r.Merged);
    }

    [Fact]
    public async Task Rerun_AfterPartialApply_DoesNotDuplicateTags()
    {
        using var db = TestDbContextFactory.Create();
        var (_, orphan, keep) = await SeedLivePair(db);
        var tag = new Tag { Id = Guid.NewGuid(), Name = "tower" };
        db.Tags.Add(tag);
        orphan.Tags.Add(tag);
        keep.Tags.Add(tag); // as if a previous interrupted run copied it already
        await db.SaveChangesAsync();
        var svc = new MmfOrphanMergeService(db, Meta(), NullLogger<MmfOrphanMergeService>.Instance);
        await svc.MergeOrphansAsync(apply: true, includeLive: true);
        var k = await db.Models.Include(m => m.Tags).SingleAsync();
        Assert.Single(k.Tags);
    }

    private static (MmfOrphanMergeJob Job, Mock<IScannerService> Scanner) Job(string dbName, Func<bool>? sync = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => (ForgeDbContext)TestDbContextFactory.Create(dbName));
        services.AddSingleton<IMetadataService>(Meta());
        services.AddScoped(sp => new MmfOrphanMergeService(sp.GetRequiredService<ForgeDbContext>(),
            sp.GetRequiredService<IMetadataService>(), NullLogger<MmfOrphanMergeService>.Instance));
        var scanner = new Mock<IScannerService>();
        var job = new MmfOrphanMergeJob(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            scanner.Object, sync ?? (() => false), NullLogger<MmfOrphanMergeJob>.Instance);
        return (job, scanner);
    }

    [Fact]
    public void Job_RefusesWhileScanRunning()
    {
        var (job, scanner) = Job(Guid.NewGuid().ToString());
        scanner.SetupGet(s => s.IsRunning).Returns(true);
        var (started, reason, _) = job.TryStart(true, true, _root, CancellationToken.None);
        Assert.False(started);
        Assert.Contains("scan", reason);
        Assert.False(LibraryMaintenanceGate.IsHeld);
    }

    [Fact]
    public void Job_RefusesWhileSyncRunning()
    {
        var (job, _) = Job(Guid.NewGuid().ToString(), () => true);
        var (started, reason, _) = job.TryStart(true, true, _root, CancellationToken.None);
        Assert.False(started);
        Assert.Contains("sync", reason);
    }

    [Fact]
    public async Task Job_RunsInBackground_WritesBackupAndReleasesGate()
    {
        var name = Guid.NewGuid().ToString();
        using (var db = TestDbContextFactory.Create(name)) await SeedLivePair(db);
        var (job, _) = Job(name);
        var reports = Path.Combine(_root, ".forgekeeper-reports");
        var (started, _, run) = job.TryStart(true, true, reports, CancellationToken.None);
        Assert.True(started);
        await run!;
        Assert.Equal("completed", job.Status.State);
        Assert.False(LibraryMaintenanceGate.IsHeld);
        Assert.Contains(Directory.GetFiles(reports), f => Path.GetFileName(f).StartsWith("mmf-orphan-merge-backup-"));
        Assert.Contains(Directory.GetFiles(reports), f => Path.GetFileName(f).StartsWith("mmf-orphan-merge-applied-"));
        using var check = TestDbContextFactory.Create(name);
        Assert.Equal(1, await check.Models.CountAsync());
    }

    [Fact]
    public async Task Scanner_DefersWhileGateHeld()
    {
        Assert.True(LibraryMaintenanceGate.TryEnter("test"));
        try
        {
            var scanner = new FileScannerService(Mock.Of<IDbContextFactory<ForgeDbContext>>(), [], Meta(),
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), NullLogger<FileScannerService>.Instance);
            var p = await scanner.ScanAsync();
            Assert.Equal("deferred", p.Status);
            Assert.False(scanner.IsRunning);
        }
        finally { LibraryMaintenanceGate.Exit("test"); }
    }
}
