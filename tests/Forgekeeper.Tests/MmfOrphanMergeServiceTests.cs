using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>#63 MMF orphan merge and #67 source assignment.</summary>
public class MmfOrphanMergeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fk-merge-" + Guid.NewGuid().ToString("N"));

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

    [Theory]
    [InlineData("object-123", null, true)]
    [InlineData("456", null, true)]
    [InlineData("3f2a9c1e-1111-2222-3333-444455556666", null, false)]
    [InlineData(null, "https://www.myminifactory.com/object/3d-print-x-1", true)]
    [InlineData(null, null, false)]
    public void IsMmfIdentity_Rules(string? id, string? url, bool expected) =>
        Assert.Equal(expected, MmfOrphanMergeService.IsMmfIdentity(id, url));

    [Fact]
    public void IsUnderDotDir_DetectsQuarantine()
    {
        Assert.True(MmfOrphanMergeService.IsUnderDotDir("/library/sources/mmf/.trash-20261009/X"));
        Assert.False(MmfOrphanMergeService.IsUnderDotDir("/library/sources/mmf/Creator/X"));
    }

    [Fact]
    public async Task Merge_CleanPair_CopiesMetadataTagsAndDeletesOrphan()
    {
        using var db = TestDbContextFactory.Create();
        var meta = Meta();
        var creator = new Creator { Id = Guid.NewGuid(), Name = "Dark Realms Forge", Source = SourceType.Mmf };
        db.Creators.Add(creator);
        var tag = new Tag { Id = Guid.NewGuid(), Name = "buildings" };
        db.Tags.Add(tag);

        var orphanDir = Dir(".trash-20261009/Dark Realms - Building 2");
        await meta.WriteAsync(orphanDir, new SourceMetadata
        {
            MetadataVersion = 1, Source = "mmf", ExternalId = "object-999", Name = "Dark Realms - Building 2",
            ExternalUrl = "https://www.myminifactory.com/object/999", Description = "A building", Tags = ["buildings", "fantasy"],
        });
        var keepDir = Dir("Dark Realms Forge/Dark Realms - Building 2");
        await meta.WriteAsync(keepDir, new SourceMetadata
        {
            MetadataVersion = 1, Source = "mmf", ExternalId = Guid.NewGuid().ToString(), Name = "Dark Realms - Building 2", Tags = [],
        });

        var orphan = new Model3D
        {
            Id = Guid.NewGuid(), CreatorId = creator.Id, Name = "Dark Realms - Building 2", Source = SourceType.Mmf,
            SourceId = "object-999", SourceUrl = "https://www.myminifactory.com/object/999", Description = "A building",
            BasePath = orphanDir, FileCount = 0, Tags = [tag], PreviewImages = ["https://x/1.jpg"],
        };
        var keep = new Model3D
        {
            Id = Guid.NewGuid(), CreatorId = creator.Id, Name = "Dark Realms – Building 2", Source = SourceType.Mmf,
            SourceId = Guid.NewGuid().ToString(), BasePath = keepDir, FileCount = 36, PreviewImages = ["a.jpg"],
        };
        db.Models.AddRange(orphan, keep);
        await db.SaveChangesAsync();

        var svc = new MmfOrphanMergeService(db, meta, NullLogger<MmfOrphanMergeService>.Instance);

        var dry = await svc.MergeOrphansAsync(apply: false);
        Assert.Equal(1, dry.Pairs);
        Assert.Equal(0, dry.Merged);
        Assert.Equal(2, await db.Models.CountAsync());

        var applied = await svc.MergeOrphansAsync(apply: true);
        Assert.Equal(1, applied.Merged);

        var models = await db.Models.Include(m => m.Tags).ToListAsync();
        var k = Assert.Single(models);
        Assert.Equal(keep.Id, k.Id);
        Assert.Equal("object-999", k.SourceId);
        Assert.Equal("A building", k.Description);
        Assert.Contains(k.Tags, t => t.Name == "buildings");
        Assert.Equal(["a.jpg"], k.PreviewImages); // local previews kept

        var onDisk = await meta.ReadAsync(keepDir);
        Assert.Equal("object-999", onDisk!.ExternalId);
        Assert.Equal("https://www.myminifactory.com/object/999", onDisk.ExternalUrl);
        Assert.Contains("fantasy", onDisk.Tags!);
    }

    [Fact]
    public async Task Merge_AmbiguousManyToOne_IsNotApplied()
    {
        using var db = TestDbContextFactory.Create();
        var creator = new Creator { Id = Guid.NewGuid(), Name = "C", Source = SourceType.Mmf };
        db.Creators.Add(creator);
        db.Models.AddRange(
            new Model3D { Id = Guid.NewGuid(), CreatorId = creator.Id, Name = "Tower", SourceId = "object-1", BasePath = "/l/.trash/Tower", FileCount = 0 },
            new Model3D { Id = Guid.NewGuid(), CreatorId = creator.Id, Name = "Tower", SourceId = "object-2", BasePath = "/l/.trash/Tower (2)", FileCount = 0 },
            new Model3D { Id = Guid.NewGuid(), CreatorId = creator.Id, Name = "Tower", SourceId = "abc", BasePath = "/l/C/Tower", FileCount = 3 });
        await db.SaveChangesAsync();

        var svc = new MmfOrphanMergeService(db, Meta(), NullLogger<MmfOrphanMergeService>.Instance);
        var r = await svc.MergeOrphansAsync(apply: true);
        Assert.Equal(0, r.Pairs);
        Assert.Equal(2, r.Ambiguous);
        Assert.Equal(3, await db.Models.CountAsync());
    }

    [Fact]
    public async Task BackfillSource_MovesNonMmfToManual()
    {
        using var db = TestDbContextFactory.Create();
        var creator = new Creator { Id = Guid.NewGuid(), Name = "C", Source = SourceType.Mmf };
        db.Creators.Add(creator);
        db.Models.AddRange(
            new Model3D { Id = Guid.NewGuid(), CreatorId = creator.Id, Name = "A", Source = SourceType.Mmf, SourceId = "object-5", BasePath = "/a" },
            new Model3D { Id = Guid.NewGuid(), CreatorId = creator.Id, Name = "B", Source = SourceType.Mmf, SourceId = Guid.NewGuid().ToString(), BasePath = "/b" });
        await db.SaveChangesAsync();

        var svc = new MmfOrphanMergeService(db, Meta(), NullLogger<MmfOrphanMergeService>.Instance);
        var r = await svc.BackfillSourceAsync(apply: true);
        Assert.Equal(1, r.ToManual);
        Assert.Equal(SourceType.Manual, (await db.Models.SingleAsync(m => m.Name == "B")).Source);
        Assert.Equal(SourceType.Mmf, (await db.Models.SingleAsync(m => m.Name == "A")).Source);
    }
}
