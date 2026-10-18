using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// One-off, idempotent backfill for multi-source attribution phases 1-3 (#91, #92, #93):
/// <list type="bullet">
/// <item>source rows for every well-known / in-use source (enum kept for compat)</item>
/// <item>models.source_entity_id linked to their source row where null</item>
/// <item>one creator_profile per creator (platform = creator.source)</item>
/// <item>one origin model_acquisition per model</item>
/// </list>
/// Creators with the same normalised name are NOT merged — see <see cref="CreatorIdentityService.GetMergeSuggestionsAsync"/>.
/// Dry-run by default; writes in batches of <see cref="BatchSize"/> so it can be stopped safely.
/// </summary>
public class MultiSourceBackfillService(ForgeDbContext db, ILogger<MultiSourceBackfillService> logger)
{
    public const int BatchSize = 50;

    public record BackfillReport(
        bool Applied,
        int SourcesCreated,
        int SourcesUpdated,
        int ModelsLinkedToSource,
        int ProfilesCreated,
        int OriginAcquisitionsCreated,
        int MergeSuggestionGroups);

    public async Task<BackfillReport> RunAsync(bool apply, CancellationToken ct = default)
    {
        var (srcCreated, srcUpdated, sourceIds) = await BackfillSourcesAsync(apply, ct);
        var linked = await LinkModelsToSourcesAsync(apply, sourceIds, ct);
        var profiles = await BackfillProfilesAsync(apply, sourceIds, ct);
        var acquisitions = await BackfillOriginAcquisitionsAsync(apply, sourceIds, ct);
        var suggestions = await new CreatorIdentityService(db).GetMergeSuggestionsAsync(ct);

        var report = new BackfillReport(apply, srcCreated, srcUpdated, linked, profiles, acquisitions, suggestions.Count);
        logger.LogInformation("Multi-source backfill {Mode}: {@Report}", apply ? "APPLIED" : "dry-run", report);
        return report;
    }

    /// <summary>Returns slug → source id (Guid.Empty for rows that would be created in dry-run).</summary>
    private async Task<(int created, int updated, Dictionary<string, Guid> ids)> BackfillSourcesAsync(bool apply, CancellationToken ct)
    {
        var existing = await db.Sources.ToListAsync(ct);
        var bySlug = existing.ToDictionary(s => s.Slug.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);

        var usedEnums = (await db.Models.Select(m => m.Source).Distinct().ToListAsync(ct))
            .Concat(await db.Creators.Select(c => c.Source).Distinct().ToListAsync(ct))
            .Select(e => e.Slug());
        var wanted = SourceCatalog.All.Select(w => w.Slug).Concat(usedEnums).Distinct(StringComparer.OrdinalIgnoreCase);

        // New rows sit next to existing ones; AutoScan off so the scanner never touches them unasked.
        var anyBase = existing.Select(s => s.BasePath).FirstOrDefault(p => !string.IsNullOrEmpty(p));
        var root = anyBase is null ? "/library/sources" : (Path.GetDirectoryName(anyBase.TrimEnd('/')) ?? "/library/sources");

        int created = 0, updated = 0;
        foreach (var slug in wanted)
        {
            var known = SourceCatalog.Find(slug);
            if (bySlug.TryGetValue(slug, out var row))
            {
                if (row.Kind is null || (row.WebsiteUrl is null && known?.WebsiteUrl is not null))
                {
                    row.Kind ??= known?.Kind ?? "platform";
                    row.WebsiteUrl ??= known?.WebsiteUrl;
                    updated++;
                }
                continue;
            }

            var src = new Source
            {
                Id = Guid.NewGuid(),
                Slug = slug,
                Name = known?.Name ?? slug,
                Kind = known?.Kind ?? "platform",
                WebsiteUrl = known?.WebsiteUrl,
                BasePath = $"{root.TrimEnd('/')}/{slug}",
                AdapterType = "GenericSourceAdapter",
                AutoScan = false,
            };
            bySlug[slug] = src;
            if (apply) db.Sources.Add(src);
            created++;
        }

        if (apply) await db.SaveChangesAsync(ct);
        return (created, updated, bySlug.ToDictionary(kv => kv.Key, kv => apply ? kv.Value.Id : (kv.Value.Id), StringComparer.OrdinalIgnoreCase));
    }

    private async Task<int> LinkModelsToSourcesAsync(bool apply, Dictionary<string, Guid> ids, CancellationToken ct)
    {
        int total = 0;
        foreach (var type in Enum.GetValues<SourceType>())
        {
            if (!ids.TryGetValue(type.Slug(), out var sid)) continue;
            var count = await db.Models.CountAsync(m => m.SourceEntityId == null && m.Source == type, ct);
            total += count;
            if (!apply || count == 0) continue;

            while (true)
            {
                var batch = await db.Models.Where(m => m.SourceEntityId == null && m.Source == type)
                    .OrderBy(m => m.Id).Take(BatchSize).ToListAsync(ct);
                if (batch.Count == 0) break;
                foreach (var m in batch) m.SourceEntityId = sid;
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }
        }
        return total;
    }

    private async Task<int> BackfillProfilesAsync(bool apply, Dictionary<string, Guid> ids, CancellationToken ct)
    {
        var withProfile = db.CreatorProfiles.Select(p => p.CreatorId);
        var query = db.Creators.Where(c => !withProfile.Contains(c.Id));
        if (!apply) return await query.CountAsync(ct);

        int total = 0;
        while (true)
        {
            var batch = await query.OrderBy(c => c.Id).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) break;
            foreach (var c in batch)
            {
                var slug = c.Source.Slug();
                db.CreatorProfiles.Add(new CreatorProfile
                {
                    Id = Guid.NewGuid(),
                    CreatorId = c.Id,
                    SourceId = ids.TryGetValue(slug, out var sid) && sid != Guid.Empty ? sid : null,
                    Platform = slug,
                    Handle = c.Name,
                    Url = c.SourceUrl,
                    ExternalId = c.ExternalId,
                });
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            total += batch.Count;
        }
        return total;
    }

    private async Task<int> BackfillOriginAcquisitionsAsync(bool apply, Dictionary<string, Guid> ids, CancellationToken ct)
    {
        var withOrigin = db.ModelAcquisitions.Where(a => a.Kind == AcquisitionKind.Origin).Select(a => a.ModelId);
        var query = db.Models.Where(m => !withOrigin.Contains(m.Id));
        if (!apply) return await query.CountAsync(ct);

        int total = 0;
        while (true)
        {
            var batch = await query.OrderBy(m => m.Id).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) break;
            foreach (var m in batch) db.ModelAcquisitions.Add(OriginFromModel(m, ids));
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            total += batch.Count;
        }
        return total;
    }

    /// <summary>Builds the origin acquisition implied by a model's legacy columns.</summary>
    public static ModelAcquisition OriginFromModel(Model3D m, IReadOnlyDictionary<string, Guid> sourceIds)
    {
        var slug = m.Source.Slug();
        Guid? sid = m.SourceEntityId ?? (sourceIds.TryGetValue(slug, out var s) && s != Guid.Empty ? s : null);
        return new ModelAcquisition
        {
            Id = Guid.NewGuid(),
            ModelId = m.Id,
            Kind = AcquisitionKind.Origin,
            SourceId = sid,
            SourceSlug = slug,
            ExternalId = m.SourceId,
            ExternalUrl = m.SourceUrl,
            AcquiredAt = m.DownloadedAt,
            License = m.LicenseType,
            OrderRef = m.AcquisitionOrderId,
            Notes = m.AcquisitionMethod is null ? "backfill" : $"backfill; method={m.AcquisitionMethod}",
        };
    }
}
