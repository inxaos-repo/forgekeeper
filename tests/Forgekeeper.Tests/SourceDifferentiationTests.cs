using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Models;
using Forgekeeper.Core.Services;
using Forgekeeper.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>Source as a first-class dimension: detection order, source keys, per-source creator identities.</summary>
public class SourceDifferentiationTests
{
    // ---- SourceDetector -------------------------------------------------

    [Fact]
    public void Folder_convention_wins_for_non_catch_all_slug()
    {
        var d = SourceDetector.Detect(new SourceEvidence
        {
            FolderSlug = "patreon", MetadataSource = "mmf", MetadataVersion = 3,
            MetadataExternalUrl = "https://www.myminifactory.com/object/3d-print-x-123",
        });
        Assert.Equal(SourceType.Patreon, d.Source);
        Assert.Equal("folder", d.Detection);
    }

    [Fact]
    public void Catch_all_folder_uses_metadata_url_domain()
    {
        var d = SourceDetector.Detect(new SourceEvidence
        {
            FolderSlug = "mmf", MetadataExternalUrl = "https://www.patreon.com/posts/12345", MetadataExternalId = "12345",
        });
        Assert.Equal(SourceType.Patreon, d.Source);
        Assert.Equal("metadata", d.Detection);
        Assert.Equal("patreon:12345", d.SourceKey);
    }

    [Fact]
    public void Writeback_v1_metadata_is_not_evidence()
    {
        // Forgekeeper's own writeback stamps source=mmf and a GUID id; that's circular.
        var d = SourceDetector.Detect(new SourceEvidence
        {
            FolderSlug = "mmf", FolderName = "Medic", MetadataSource = "mmf", MetadataVersion = 1,
            MetadataExternalId = Guid.NewGuid().ToString(), PluginSourceId = Guid.NewGuid().ToString(),
        });
        Assert.Equal(SourceType.Unknown, d.Source);
        Assert.Null(d.SourceKey);
    }

    [Fact]
    public void Plugin_written_v2_metadata_is_evidence()
    {
        var d = SourceDetector.Detect(new SourceEvidence
        {
            FolderSlug = "mmf", MetadataSource = "mmf", MetadataVersion = 3, MetadataExternalId = "597143",
        });
        Assert.Equal(SourceType.Mmf, d.Source);
        Assert.Equal("metadata", d.Detection);
        Assert.Equal("mmf:597143", d.SourceKey);
    }

    [Fact]
    public void Mmf_id_from_plugin_data_and_object_prefix_normalizes()
    {
        var d = SourceDetector.Detect(new SourceEvidence { FolderSlug = "mmf", PluginSourceId = "object-783270" });
        Assert.Equal(SourceType.Mmf, d.Source);
        Assert.Equal("plugin", d.Detection);
        Assert.Equal("mmf:783270", d.SourceKey);
    }

    [Fact]
    public void Google_drive_export_suffix_is_not_an_mmf_id()
    {
        var d = SourceDetector.Detect(new SourceEvidence
        {
            FolderSlug = "mmf", FolderName = "Welcome Pack-20240109T045122Z-001",
            ParsedSourceId = "001", PluginSourceId = "001",
        });
        Assert.Equal(SourceType.Unknown, d.Source);
        Assert.Null(d.SourceKey);
        Assert.Contains("Drive", d.Reason);
    }

    [Theory]
    [InlineData("123", true)]
    [InlineData("object-123", true)]
    [InlineData("001", false)]
    [InlineData("0", false)]
    [InlineData("07d21d14-b2fb-4bb0-9ae3-0cb2d1e1d0aa", false)]
    public void IsMmfId(string id, bool expected) => Assert.Equal(expected, SourceDetector.IsMmfId(id));

    [Theory]
    [InlineData("https://cults3d.com/en/3d-model/x", SourceType.Cults3d)]
    [InlineData("https://thangs.com/designer/x/3d-model/y-1", SourceType.Thangs)]
    [InlineData("https://someone.gumroad.com/l/abc", SourceType.Gumroad)]
    [InlineData("https://example.com/x", null)]
    public void FromUrl(string url, SourceType? expected) => Assert.Equal(expected, SourceDetector.FromUrl(url));

    [Fact]
    public void Manual_folder_never_gets_a_source_key()
    {
        var d = SourceDetector.Detect(new SourceEvidence { FolderSlug = "manual", PluginSourceId = "12345" });
        Assert.Equal(SourceType.Manual, d.Source);
        Assert.Null(d.SourceKey);
    }

    [Fact]
    public void SlugFromPath() =>
        Assert.Equal("patreon", SourceRecategorizeService.SlugFromPath("/library/sources/Patreon/Creator/Model"));

    // ---- Creator identities ---------------------------------------------

    [Fact]
    public async Task Resolver_links_new_source_identity_to_canonical()
    {
        using var db = TestDbContextFactory.Create();
        var mmf = new Creator { Id = Guid.NewGuid(), Name = "Print Minis", Source = SourceType.Mmf };
        db.Creators.Add(mmf);
        await db.SaveChangesAsync();

        var patreon = await CreatorIdentityResolver.ResolveAsync(db, "print minis", SourceType.Patreon);
        await db.SaveChangesAsync();

        Assert.NotEqual(mmf.Id, patreon.Id);
        Assert.Equal(mmf.Id, patreon.CanonicalCreatorId);
        var ids = await CreatorIdentityResolver.IdentityIdsAsync(db, patreon.Id);
        Assert.Equal(2, ids.Count);

        var again = await CreatorIdentityResolver.ResolveAsync(db, "Print Minis", SourceType.Mmf);
        Assert.Equal(mmf.Id, again.Id);
    }

    // ---- Re-categorize command ------------------------------------------

    private static async Task<(Forgekeeper.Infrastructure.Data.ForgeDbContext db, Creator creator)> Seed()
    {
        var db = TestDbContextFactory.Create();
        var c = new Creator { Id = Guid.NewGuid(), Name = "Print Minis", Source = SourceType.Mmf };
        var dupCase = new Creator { Id = Guid.NewGuid(), Name = "print minis", Source = SourceType.Patreon, CreatedAt = DateTime.UtcNow.AddDays(1) };
        db.Creators.AddRange(c, dupCase);
        db.Models.AddRange(
            new Model3D { Id = Guid.NewGuid(), Name = "Real", CreatorId = c.Id, Source = SourceType.Mmf, SourceId = "123",
                BasePath = "/nope/sources/mmf/Print Minis/Real - 123" },
            new Model3D { Id = Guid.NewGuid(), Name = "Medic", CreatorId = c.Id, Source = SourceType.Manual,
                SourceId = Guid.NewGuid().ToString(), BasePath = "/nope/sources/mmf/Print Minis/Medic" },
            new Model3D { Id = Guid.NewGuid(), Name = "Pack", CreatorId = c.Id, Source = SourceType.Mmf, SourceId = "001",
                BasePath = "/nope/sources/mmf/Print Minis/Pack-20240226T222810Z-001" },
            new Model3D { Id = Guid.NewGuid(), Name = "Real copy", CreatorId = c.Id, Source = SourceType.Mmf, SourceId = "object-123",
                BasePath = "/nope/sources/mmf/unknown/Real copy" });
        await db.SaveChangesAsync();
        return (db, c);
    }

    [Fact]
    public async Task Recategorize_dry_run_reports_and_writes_nothing()
    {
        var (db, _) = await Seed();
        using (db)
        {
            var svc = new SourceRecategorizeService(db, new MetadataService(NullLogger<MetadataService>.Instance),
                NullLogger<SourceRecategorizeService>.Instance);
            var r = await svc.RunAsync(apply: false, readMetadataFiles: false);

            Assert.False(r.Applied);
            Assert.Equal(1, r.Transitions["Manual->Unknown"]);
            Assert.Equal(1, r.Transitions["Mmf->Unknown"]);
            Assert.Single(r.SourceKeyConflicts);          // object-123 duplicates 123
            Assert.Equal(1, r.CanonicalLinks);

            db.ChangeTracker.Clear();
            Assert.Equal(0, await db.Models.CountAsync(m => m.Source == SourceType.Unknown));
            Assert.Equal(0, await db.Models.CountAsync(m => m.SourceKey != null));
            Assert.Equal(0, await db.Creators.CountAsync(c => c.CanonicalCreatorId != null));
            Assert.Contains("DRY RUN", SourceRecategorizeService.Format(r));
        }
    }

    [Fact]
    public async Task Recategorize_apply_moves_models_to_per_source_identity()
    {
        var (db, mmfCreator) = await Seed();
        using (db)
        {
            var svc = new SourceRecategorizeService(db, new MetadataService(NullLogger<MetadataService>.Instance),
                NullLogger<SourceRecategorizeService>.Instance);
            await svc.RunAsync(apply: true, readMetadataFiles: false);
            db.ChangeTracker.Clear();

            var medic = await db.Models.Include(m => m.Creator).SingleAsync(m => m.Name == "Medic");
            Assert.Equal(SourceType.Unknown, medic.Source);
            Assert.Equal(SourceType.Unknown, medic.Creator.Source);
            Assert.Equal(mmfCreator.Id, medic.Creator.CanonicalCreatorId);

            var real = await db.Models.SingleAsync(m => m.Name == "Real");
            Assert.Equal("mmf:123", real.SourceKey);
            Assert.Null((await db.Models.SingleAsync(m => m.Name == "Real copy")).SourceKey);

            var ids = await CreatorIdentityResolver.IdentityIdsAsync(db, mmfCreator.Id);
            Assert.Equal(3, ids.Count); // Mmf root + case-dup Patreon + new Unknown identity
        }
    }

    [Fact]
    public async Task Override_is_never_recategorized()
    {
        var (db, _) = await Seed();
        using (db)
        {
            var medic = await db.Models.SingleAsync(m => m.Name == "Medic");
            medic.Source = SourceType.Patreon;
            medic.SourceDetection = "override";
            await db.SaveChangesAsync();
            var svc = new SourceRecategorizeService(db, new MetadataService(NullLogger<MetadataService>.Instance),
                NullLogger<SourceRecategorizeService>.Instance);
            await svc.RunAsync(apply: true, readMetadataFiles: false);
            db.ChangeTracker.Clear();
            Assert.Equal(SourceType.Patreon, (await db.Models.SingleAsync(m => m.Name == "Medic")).Source);
        }
    }
}
