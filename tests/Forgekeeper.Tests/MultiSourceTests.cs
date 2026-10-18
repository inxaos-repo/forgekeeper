using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>Unit tests for multi-source attribution phases 1-3 (#91, #92, #93).</summary>
public class MultiSourceUnitTests
{
    private static (Creator c, Model3D m) Seed(ForgeDbContext db, string name, SourceType src, int models = 1)
    {
        var c = new Creator { Id = Guid.NewGuid(), Name = name, Source = src, ModelCount = models, SourceUrl = $"https://x/{name}" };
        db.Creators.Add(c);
        Model3D? first = null;
        for (var i = 0; i < models; i++)
        {
            var m = new Model3D
            {
                Id = Guid.NewGuid(), Name = $"{name}-{i}", CreatorId = c.Id, Source = src,
                BasePath = $"/t/{Guid.NewGuid():N}", SourceId = $"ext{i}", SourceUrl = $"https://x/m{i}",
                LicenseType = "personal", AcquisitionOrderId = "ord-1", DownloadedAt = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            };
            first ??= m;
            db.Models.Add(m);
        }
        return (c, first!);
    }

    [Theory]
    [InlineData("Loot Studios", "lootstudios")]
    [InlineData("loot-studios ", "lootstudios")]
    [InlineData("LOOT_STUDIOS!", "lootstudios")]
    [InlineData("Café 3D", "cafe3d")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeName_collapses_case_punctuation_and_accents(string? input, string expected)
        => Assert.Equal(expected, SourceCatalog.NormalizeName(input));

    [Fact]
    public void SourceCatalog_has_slug_for_every_enum_value()
    {
        foreach (var t in Enum.GetValues<SourceType>())
            Assert.NotNull(SourceCatalog.Find(t.Slug()));
        Assert.Equal("channel", SourceCatalog.Find("telegram")!.Kind);
    }

    [Fact]
    public void OriginFromModel_maps_legacy_columns()
    {
        var m = new Model3D { Id = Guid.NewGuid(), Source = SourceType.Mmf, SourceId = "123", SourceUrl = "u", LicenseType = "cc", AcquisitionOrderId = "o", AcquisitionMethod = AcquisitionMethod.Purchase };
        var sid = Guid.NewGuid();
        var a = MultiSourceBackfillService.OriginFromModel(m, new Dictionary<string, Guid> { ["mmf"] = sid });
        Assert.Equal(AcquisitionKind.Origin, a.Kind);
        Assert.Equal(sid, a.SourceId);
        Assert.Equal("mmf", a.SourceSlug);
        Assert.Equal("123", a.ExternalId);
        Assert.Equal("o", a.OrderRef);
        Assert.Contains("Purchase", a.Notes);
    }

    [Fact]
    public async Task Backfill_dry_run_writes_nothing()
    {
        await using var db = TestDbContextFactory.Create();
        Seed(db, "A", SourceType.Mmf, 2);
        await db.SaveChangesAsync();

        var report = await new MultiSourceBackfillService(db, NullLogger<MultiSourceBackfillService>.Instance).RunAsync(apply: false);

        Assert.False(report.Applied);
        Assert.Equal(2, report.OriginAcquisitionsCreated);
        Assert.Equal(1, report.ProfilesCreated);
        Assert.True(report.SourcesCreated >= SourceCatalog.All.Count);
        Assert.Equal(0, await db.Sources.CountAsync());
        Assert.Equal(0, await db.ModelAcquisitions.CountAsync());
        Assert.Equal(0, await db.CreatorProfiles.CountAsync());
    }

    [Fact]
    public async Task Backfill_apply_is_idempotent_and_never_merges()
    {
        await using var db = TestDbContextFactory.Create();
        var (a, _) = Seed(db, "Loot Studios", SourceType.Mmf, 2);
        var (b, _) = Seed(db, "loot-studios", SourceType.Patreon, 1);
        await db.SaveChangesAsync();
        var svc = new MultiSourceBackfillService(db, NullLogger<MultiSourceBackfillService>.Instance);

        var first = await svc.RunAsync(apply: true);
        Assert.Equal(3, first.OriginAcquisitionsCreated);
        Assert.Equal(2, first.ProfilesCreated);
        Assert.Equal(3, first.ModelsLinkedToSource);
        Assert.Equal(1, first.MergeSuggestionGroups);

        var second = await svc.RunAsync(apply: true);
        Assert.Equal(0, second.OriginAcquisitionsCreated);
        Assert.Equal(0, second.ProfilesCreated);
        Assert.Equal(0, second.SourcesCreated);
        Assert.Equal(0, second.ModelsLinkedToSource);

        db.ChangeTracker.Clear();
        Assert.Equal(3, await db.ModelAcquisitions.CountAsync(x => x.Kind == AcquisitionKind.Origin));
        Assert.All(await db.Models.ToListAsync(), m => Assert.NotNull(m.SourceEntityId));
        Assert.Equal(0, await db.Creators.CountAsync(c => c.MergedIntoId != null));
        Assert.Equal(0, await db.CreatorMerges.CountAsync());
        var mmfProfile = await db.CreatorProfiles.SingleAsync(p => p.CreatorId == a.Id);
        Assert.Equal("mmf", mmfProfile.Platform);
        Assert.NotNull(mmfProfile.SourceId);
        Assert.False((await db.Sources.SingleAsync(s => s.Slug == "telegram")).AutoScan);
        _ = b;
    }

    [Fact]
    public async Task Suggestions_group_same_normalized_name_across_sources()
    {
        await using var db = TestDbContextFactory.Create();
        var (a, _) = Seed(db, "Loot Studios", SourceType.Mmf, 3);
        var (b, _) = Seed(db, "loot_studios", SourceType.Patreon, 1);
        Seed(db, "Someone Else", SourceType.Mmf, 1);
        await db.SaveChangesAsync();

        var s = await new CreatorIdentityService(db).GetMergeSuggestionsAsync();

        var group = Assert.Single(s);
        Assert.Equal(a.Id, group.SuggestedSurvivorId);
        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), group.Creators.Select(c => c.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task Merge_then_unmerge_restores_everything()
    {
        await using var db = TestDbContextFactory.Create();
        var (a, _) = Seed(db, "Loot Studios", SourceType.Mmf, 2);
        var (b, bm) = Seed(db, "loot_studios", SourceType.Patreon, 1);
        db.CreatorProfiles.Add(new CreatorProfile { Id = Guid.NewGuid(), CreatorId = b.Id, Platform = "patreon" });
        await db.SaveChangesAsync();
        var svc = new CreatorIdentityService(db);

        var merge = await svc.MergeAsync(a.Id, b.Id, "same studio", "test");

        db.ChangeTracker.Clear();
        Assert.Equal(a.Id, (await db.Models.FindAsync(bm.Id))!.CreatorId);
        Assert.Equal(a.Id, (await db.Creators.FindAsync(b.Id))!.MergedIntoId);
        Assert.Equal(3, (await db.Creators.FindAsync(a.Id))!.ModelCount);
        Assert.Equal(2, await db.CreatorProfiles.CountAsync(p => p.CreatorId == a.Id) + 1); // patreon moved; mmf profile not seeded
        Assert.Contains(await db.CreatorAliases.Where(x => x.CreatorId == a.Id).ToListAsync(), x => x.Kind == "merged");
        Assert.Empty(await svc.GetMergeSuggestionsAsync());

        await Assert.ThrowsAsync<CreatorIdentityService.MergeException>(() => svc.MergeAsync(a.Id, b.Id, null, null));

        await svc.UnmergeAsync(merge.Id);

        db.ChangeTracker.Clear();
        Assert.Equal(b.Id, (await db.Models.FindAsync(bm.Id))!.CreatorId);
        Assert.Null((await db.Creators.FindAsync(b.Id))!.MergedIntoId);
        Assert.Equal(2, (await db.Creators.FindAsync(a.Id))!.ModelCount);
        Assert.Equal(1, (await db.Creators.FindAsync(b.Id))!.ModelCount);
        Assert.Equal(1, await db.CreatorProfiles.CountAsync(p => p.CreatorId == b.Id));
        Assert.Empty(await db.CreatorAliases.ToListAsync());
        Assert.NotNull((await db.CreatorMerges.FindAsync(merge.Id))!.UnmergedAt);

        var ex = await Assert.ThrowsAsync<CreatorIdentityService.MergeException>(() => svc.UnmergeAsync(merge.Id));
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task Merge_rejects_self_and_chains()
    {
        await using var db = TestDbContextFactory.Create();
        var (a, _) = Seed(db, "A", SourceType.Mmf);
        var (b, _) = Seed(db, "B", SourceType.Mmf);
        var (c, _) = Seed(db, "C", SourceType.Mmf);
        await db.SaveChangesAsync();
        var svc = new CreatorIdentityService(db);

        await Assert.ThrowsAsync<CreatorIdentityService.MergeException>(() => svc.MergeAsync(a.Id, a.Id, null, null));
        await svc.MergeAsync(b.Id, c.Id, null, null);
        var ex = await Assert.ThrowsAsync<CreatorIdentityService.MergeException>(() => svc.MergeAsync(a.Id, b.Id, null, null));
        Assert.Equal(409, ex.StatusCode);
        ex = await Assert.ThrowsAsync<CreatorIdentityService.MergeException>(() => svc.MergeAsync(c.Id, a.Id, null, null));
        Assert.Equal(409, ex.StatusCode);
    }
}

/// <summary>API integration tests for multi-source attribution endpoints.</summary>
public class MultiSourceApiTests : IClassFixture<ForgeTestFactory>
{
    private readonly HttpClient _client;
    private readonly ForgeTestFactory _factory;

    public MultiSourceApiTests(ForgeTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<(Guid modelId, Guid creatorId)> SeedAsync(string name, SourceType src = SourceType.Thangs)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var c = new Creator { Id = Guid.NewGuid(), Name = name, Source = src, ModelCount = 1 };
        var m = new Model3D { Id = Guid.NewGuid(), Name = $"M_{Guid.NewGuid():N}", CreatorId = c.Id, Source = src, BasePath = $"/t/{Guid.NewGuid():N}" };
        db.Creators.Add(c);
        db.Models.Add(m);
        await db.SaveChangesAsync();
        return (m.Id, c.Id);
    }

    [Fact]
    public async Task Profiles_crud()
    {
        var (_, cid) = await SeedAsync($"P_{Guid.NewGuid():N}");

        var created = await _client.PostAsJsonAsync($"/api/v1/creators/{cid}/profiles", new { platform = "Patreon", handle = "studio", url = "https://patreon.com/studio" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var pid = body.GetProperty("id").GetGuid();
        Assert.Equal("patreon", body.GetProperty("platform").GetString());

        var put = await _client.PutAsJsonAsync($"/api/v1/creators/{cid}/profiles/{pid}", new { handle = "studio2" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var list = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/creators/{cid}/profiles");
        Assert.Equal("studio2", list[0].GetProperty("handle").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/v1/creators/{cid}/profiles/{pid}")).StatusCode);
        Assert.Equal(0, (await _client.GetFromJsonAsync<JsonElement>($"/api/v1/creators/{cid}/profiles")).GetArrayLength());

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"/api/v1/creators/{cid}/profiles", new { handle = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/creators/{Guid.NewGuid()}/profiles")).StatusCode);
    }

    [Fact]
    public async Task Merge_suggestion_merge_unmerge_roundtrip()
    {
        var stem = $"Dup{Guid.NewGuid():N}";
        var (_, a) = await SeedAsync(stem, SourceType.Mmf);
        var (mb, b) = await SeedAsync(stem.ToUpperInvariant() + "!", SourceType.Patreon);

        var sugg = await _client.GetFromJsonAsync<JsonElement>("/api/v1/creators/merge-suggestions");
        Assert.Contains(sugg.EnumerateArray(), g => g.GetProperty("creators").EnumerateArray().Any(c => c.GetProperty("id").GetGuid() == b));

        var merge = await _client.PostAsJsonAsync($"/api/v1/creators/{a}/merge", new { mergedId = b, reason = "test" });
        Assert.Equal(HttpStatusCode.OK, merge.StatusCode);
        var mergeId = (await merge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Existing /creators/{id} still works for both (backward compatible shape).
        var survivor = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/creators/{a}");
        Assert.Equal(2, survivor.GetProperty("modelCount").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/v1/creators/{b}")).StatusCode);

        var again = await _client.PostAsJsonAsync($"/api/v1/creators/{a}/merge", new { mergedId = b });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var history = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/creators/merges?creatorId={b}");
        Assert.Equal(1, history.GetArrayLength());

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/api/v1/creators/merges/{mergeId}/unmerge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/v1/creators/merges/{mergeId}/unmerge", null)).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        Assert.Equal(b, (await db.Models.AsNoTracking().SingleAsync(m => m.Id == mb)).CreatorId);
    }

    [Fact]
    public async Task Merge_unknown_creator_is_404()
    {
        var (_, a) = await SeedAsync($"U_{Guid.NewGuid():N}");
        var r = await _client.PostAsJsonAsync($"/api/v1/creators/{a}/merge", new { mergedId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Acquisitions_crud_origin_and_via()
    {
        var (mid, _) = await SeedAsync($"A_{Guid.NewGuid():N}");

        var origin = await _client.PostAsJsonAsync($"/api/v1/models/{mid}/acquisitions", new { kind = "origin", sourceSlug = "mmf", externalId = "42" });
        Assert.Equal(HttpStatusCode.Created, origin.StatusCode);
        var via = await _client.PostAsJsonAsync($"/api/v1/models/{mid}/acquisitions", new { kind = "via", sourceSlug = "telegram", acquiredAt = "2026-01-02T00:00:00Z", fileSelector = "{\"paths\":[\"a.stl\"]}" });
        Assert.Equal(HttpStatusCode.Created, via.StatusCode);
        var viaId = (await via.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var list = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/models/{mid}/acquisitions");
        Assert.Equal(2, list.GetArrayLength());
        Assert.Contains(list.EnumerateArray(), a => a.GetProperty("kind").GetString() == "via" && a.GetProperty("sourceSlug").GetString() == "telegram");

        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync($"/api/v1/models/{mid}/acquisitions/{viaId}", new { notes = "channel X" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"/api/v1/models/{mid}/acquisitions", new { kind = "stolen" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"/api/v1/models/{mid}/acquisitions", new { kind = "via", fileSelector = "{not json" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/v1/models/{mid}/acquisitions/{viaId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/models/{Guid.NewGuid()}/acquisitions")).StatusCode);

        // Existing model detail endpoint unaffected.
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/v1/models/{mid}")).StatusCode);
    }

    [Fact]
    public async Task Backfill_endpoint_defaults_to_dry_run()
    {
        var r = await _client.PostAsync("/api/v1/maintenance/multi-source/backfill", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("applied").GetBoolean());
    }
}
