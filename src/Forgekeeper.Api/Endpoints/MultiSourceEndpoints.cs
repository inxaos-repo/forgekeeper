using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Forgekeeper.Api.Endpoints;

/// <summary>
/// Multi-source attribution phases 1-3 (#91, #92, #93). All routes are additive; existing
/// /creators and /models responses are unchanged.
/// </summary>
public static class MultiSourceEndpoints
{
    public record CreatorProfileRequest(string? Platform, Guid? SourceId, string? Handle, string? Url, string? ExternalId);
    public record CreatorAliasRequest(string Alias, string? Kind, Guid? SourceId);
    public record MergeRequest(Guid MergedId, string? Reason, string? MergedBy);
    public record AcquisitionRequest(
        string? Kind, Guid? SourceId, string? SourceSlug, string? ExternalId, string? ExternalUrl,
        DateTime? AcquiredAt, string? License, string? Tier, string? OrderRef, string? PledgeRef,
        string? FileSelector, string? Notes);

    public static void MapMultiSourceEndpoints(this IEndpointRouteBuilder app)
    {
        var creators = app.MapGroup("/api/v1/creators").WithTags("Creator identity");

        // ── Profiles ────────────────────────────────────────────
        creators.MapGet("/{id:guid}/profiles", async (Guid id, ForgeDbContext db, CancellationToken ct) =>
        {
            if (!await db.Creators.AnyAsync(c => c.Id == id, ct)) return Results.NotFound();
            var list = await db.CreatorProfiles.AsNoTracking().Where(p => p.CreatorId == id)
                .OrderBy(p => p.Platform).ThenBy(p => p.Handle).ToListAsync(ct);
            return Results.Ok(list.Select(ToDto));
        }).WithName("ListCreatorProfiles");

        creators.MapPost("/{id:guid}/profiles", async (Guid id, [FromBody] CreatorProfileRequest req, ForgeDbContext db, CancellationToken ct) =>
        {
            if (!await db.Creators.AnyAsync(c => c.Id == id, ct)) return Results.NotFound();
            var (platform, sourceId, error) = await ResolveSourceAsync(db, req.Platform, req.SourceId, ct);
            if (error is not null) return Results.BadRequest(new { error });

            var profile = new CreatorProfile
            {
                Id = Guid.NewGuid(), CreatorId = id, Platform = platform!, SourceId = sourceId,
                Handle = req.Handle, Url = req.Url, ExternalId = req.ExternalId,
            };
            db.CreatorProfiles.Add(profile);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/creators/{id}/profiles/{profile.Id}", ToDto(profile));
        }).WithName("CreateCreatorProfile");

        creators.MapPut("/{id:guid}/profiles/{profileId:guid}", async (Guid id, Guid profileId, [FromBody] CreatorProfileRequest req, ForgeDbContext db, CancellationToken ct) =>
        {
            var profile = await db.CreatorProfiles.FirstOrDefaultAsync(p => p.Id == profileId && p.CreatorId == id, ct);
            if (profile is null) return Results.NotFound();
            if (req.Platform is not null || req.SourceId is not null)
            {
                var (platform, sourceId, error) = await ResolveSourceAsync(db, req.Platform, req.SourceId, ct);
                if (error is not null) return Results.BadRequest(new { error });
                profile.Platform = platform!;
                profile.SourceId = sourceId;
            }
            if (req.Handle is not null) profile.Handle = req.Handle;
            if (req.Url is not null) profile.Url = req.Url;
            if (req.ExternalId is not null) profile.ExternalId = req.ExternalId;
            profile.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(profile));
        }).WithName("UpdateCreatorProfile");

        creators.MapDelete("/{id:guid}/profiles/{profileId:guid}", async (Guid id, Guid profileId, ForgeDbContext db, CancellationToken ct) =>
        {
            var profile = await db.CreatorProfiles.FirstOrDefaultAsync(p => p.Id == profileId && p.CreatorId == id, ct);
            if (profile is null) return Results.NotFound();
            db.CreatorProfiles.Remove(profile);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("DeleteCreatorProfile");

        // ── Aliases ─────────────────────────────────────────────
        creators.MapGet("/{id:guid}/aliases", async (Guid id, ForgeDbContext db, CancellationToken ct) =>
        {
            if (!await db.Creators.AnyAsync(c => c.Id == id, ct)) return Results.NotFound();
            var list = await db.CreatorAliases.AsNoTracking().Where(a => a.CreatorId == id).OrderBy(a => a.Alias).ToListAsync(ct);
            return Results.Ok(list.Select(a => new { a.Id, a.CreatorId, a.Alias, a.NormalizedAlias, a.Kind, a.SourceId, a.CreatedAt }));
        }).WithName("ListCreatorAliases");

        creators.MapPost("/{id:guid}/aliases", async (Guid id, [FromBody] CreatorAliasRequest req, ForgeDbContext db, CancellationToken ct) =>
        {
            if (!await db.Creators.AnyAsync(c => c.Id == id, ct)) return Results.NotFound();
            var key = SourceCatalog.NormalizeName(req.Alias);
            if (key.Length == 0) return Results.BadRequest(new { error = "alias is required" });
            if (await db.CreatorAliases.AnyAsync(a => a.CreatorId == id && a.NormalizedAlias == key, ct))
                return Results.Conflict(new { error = "alias already exists" });
            var alias = new CreatorAlias { Id = Guid.NewGuid(), CreatorId = id, Alias = req.Alias.Trim(), NormalizedAlias = key, Kind = req.Kind ?? "name", SourceId = req.SourceId };
            db.CreatorAliases.Add(alias);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/creators/{id}/aliases/{alias.Id}", new { alias.Id, alias.CreatorId, alias.Alias, alias.NormalizedAlias, alias.Kind, alias.SourceId });
        }).WithName("CreateCreatorAlias");

        creators.MapDelete("/{id:guid}/aliases/{aliasId:guid}", async (Guid id, Guid aliasId, ForgeDbContext db, CancellationToken ct) =>
        {
            var alias = await db.CreatorAliases.FirstOrDefaultAsync(a => a.Id == aliasId && a.CreatorId == id, ct);
            if (alias is null) return Results.NotFound();
            db.CreatorAliases.Remove(alias);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("DeleteCreatorAlias");

        // ── Merges ──────────────────────────────────────────────
        creators.MapGet("/merge-suggestions", async (CreatorIdentityService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetMergeSuggestionsAsync(ct))).WithName("ListCreatorMergeSuggestions");

        creators.MapPost("/{id:guid}/merge", async (Guid id, [FromBody] MergeRequest req, CreatorIdentityService svc, CancellationToken ct) =>
        {
            try { return Results.Ok(ToDto(await svc.MergeAsync(id, req.MergedId, req.Reason, req.MergedBy, ct))); }
            catch (CreatorIdentityService.MergeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
        }).WithName("MergeCreators");

        creators.MapGet("/merges", async ([FromQuery] Guid? creatorId, ForgeDbContext db, CancellationToken ct) =>
        {
            var q = db.CreatorMerges.AsNoTracking().AsQueryable();
            if (creatorId is { } cid) q = q.Where(m => m.SurvivorId == cid || m.MergedId == cid);
            var list = await q.OrderByDescending(m => m.MergedAt).Take(500).ToListAsync(ct);
            return Results.Ok(list.Select(ToDto));
        }).WithName("ListCreatorMerges");

        creators.MapPost("/merges/{mergeId:guid}/unmerge", async (Guid mergeId, CreatorIdentityService svc, CancellationToken ct) =>
        {
            try { return Results.Ok(ToDto(await svc.UnmergeAsync(mergeId, ct))); }
            catch (CreatorIdentityService.MergeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
        }).WithName("UnmergeCreators");

        // ── Model acquisitions ──────────────────────────────────
        var models = app.MapGroup("/api/v1/models").WithTags("Model acquisitions");

        models.MapGet("/{id:guid}/acquisitions", async (Guid id, ForgeDbContext db, CancellationToken ct) =>
        {
            if (!await db.Models.AnyAsync(m => m.Id == id, ct)) return Results.NotFound();
            var list = await db.ModelAcquisitions.AsNoTracking().Where(a => a.ModelId == id)
                .OrderBy(a => a.Kind).ThenBy(a => a.AcquiredAt).ToListAsync(ct);
            return Results.Ok(list.Select(ToDto));
        }).WithName("ListModelAcquisitions");

        models.MapPost("/{id:guid}/acquisitions", async (Guid id, [FromBody] AcquisitionRequest req, ForgeDbContext db, CancellationToken ct) =>
        {
            if (!await db.Models.AnyAsync(m => m.Id == id, ct)) return Results.NotFound();
            var acq = new ModelAcquisition { Id = Guid.NewGuid(), ModelId = id };
            var error = await ApplyAsync(db, acq, req, ct);
            if (error is not null) return Results.BadRequest(new { error });
            db.ModelAcquisitions.Add(acq);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/models/{id}/acquisitions/{acq.Id}", ToDto(acq));
        }).WithName("CreateModelAcquisition");

        models.MapPut("/{id:guid}/acquisitions/{acqId:guid}", async (Guid id, Guid acqId, [FromBody] AcquisitionRequest req, ForgeDbContext db, CancellationToken ct) =>
        {
            var acq = await db.ModelAcquisitions.FirstOrDefaultAsync(a => a.Id == acqId && a.ModelId == id, ct);
            if (acq is null) return Results.NotFound();
            var error = await ApplyAsync(db, acq, req, ct);
            if (error is not null) return Results.BadRequest(new { error });
            acq.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(acq));
        }).WithName("UpdateModelAcquisition");

        models.MapDelete("/{id:guid}/acquisitions/{acqId:guid}", async (Guid id, Guid acqId, ForgeDbContext db, CancellationToken ct) =>
        {
            var acq = await db.ModelAcquisitions.FirstOrDefaultAsync(a => a.Id == acqId && a.ModelId == id, ct);
            if (acq is null) return Results.NotFound();
            db.ModelAcquisitions.Remove(acq);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("DeleteModelAcquisition");

        // ── Backfill (dry-run by default) ───────────────────────
        app.MapPost("/api/v1/maintenance/multi-source/backfill", async ([FromQuery] bool? apply, MultiSourceBackfillService svc, CancellationToken ct) =>
            Results.Ok(await svc.RunAsync(apply == true, ct)))
            .WithTags("Maintenance").WithName("MultiSourceBackfill");
    }

    private static async Task<(string? platform, Guid? sourceId, string? error)> ResolveSourceAsync(
        ForgeDbContext db, string? slug, Guid? sourceId, CancellationToken ct)
    {
        if (sourceId is { } sid)
        {
            var src = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct);
            return src is null ? (null, null, "unknown sourceId") : (src.Slug, src.Id, null);
        }
        if (string.IsNullOrWhiteSpace(slug)) return (null, null, "platform or sourceId is required");
        var bySlug = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Slug == slug.ToLower(), ct);
        return (slug.Trim().ToLowerInvariant(), bySlug?.Id, null);
    }

    private static async Task<string?> ApplyAsync(ForgeDbContext db, ModelAcquisition acq, AcquisitionRequest req, CancellationToken ct)
    {
        if (req.Kind is not null)
        {
            if (!Enum.TryParse<AcquisitionKind>(req.Kind, ignoreCase: true, out var kind))
                return "kind must be 'origin' or 'via'";
            acq.Kind = kind;
        }
        if (req.SourceId is not null || req.SourceSlug is not null)
        {
            var (slug, sid, error) = await ResolveSourceAsync(db, req.SourceSlug, req.SourceId, ct);
            if (error is not null) return error;
            acq.SourceSlug = slug;
            acq.SourceId = sid;
        }
        if (req.ExternalId is not null) acq.ExternalId = req.ExternalId;
        if (req.ExternalUrl is not null) acq.ExternalUrl = req.ExternalUrl;
        if (req.AcquiredAt is not null) acq.AcquiredAt = req.AcquiredAt;
        if (req.License is not null) acq.License = req.License;
        if (req.Tier is not null) acq.Tier = req.Tier;
        if (req.OrderRef is not null) acq.OrderRef = req.OrderRef;
        if (req.PledgeRef is not null) acq.PledgeRef = req.PledgeRef;
        if (req.FileSelector is not null)
        {
            try { System.Text.Json.JsonDocument.Parse(req.FileSelector).Dispose(); }
            catch (System.Text.Json.JsonException) { return "fileSelector must be valid JSON"; }
            acq.FileSelector = req.FileSelector;
        }
        if (req.Notes is not null) acq.Notes = req.Notes;
        return null;
    }

    private static object ToDto(CreatorProfile p) => new
    {
        p.Id, p.CreatorId, p.SourceId, p.Platform, p.Handle, p.Url, p.ExternalId, p.CreatedAt, p.UpdatedAt,
    };

    private static object ToDto(CreatorMerge m) => new
    {
        m.Id, m.SurvivorId, m.MergedId, m.Reason, m.MergedBy, m.MergedAt, m.UnmergedAt,
        Snapshot = System.Text.Json.JsonDocument.Parse(m.Snapshot).RootElement,
    };

    private static object ToDto(ModelAcquisition a) => new
    {
        a.Id, a.ModelId, Kind = a.Kind.ToString().ToLowerInvariant(), a.SourceId, a.SourceSlug,
        a.ExternalId, a.ExternalUrl, a.AcquiredAt, a.License, a.Tier, a.OrderRef, a.PledgeRef,
        a.FileSelector, a.Notes, a.CreatedAt, a.UpdatedAt,
    };
}
