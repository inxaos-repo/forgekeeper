using System.Text.Json;
using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// Multi-source attribution phase 2 (#92): creator merge suggestions and reversible merges.
/// Nothing is ever merged automatically — suggestions are advisory only.
/// Each merge/unmerge is a single SaveChanges (atomic).
/// </summary>
public class CreatorIdentityService(ForgeDbContext db)
{
    public record SuggestedCreator(Guid Id, string Name, string Source, int ModelCount);
    public record MergeSuggestion(string NormalizedName, Guid SuggestedSurvivorId, List<SuggestedCreator> Creators);

    public record MergeSnapshot(List<Guid> ModelIds, List<Guid> ProfileIds, List<Guid> AliasIds, Guid? CreatedAliasId);

    public class MergeException(string message, int statusCode = 400) : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
    }

    /// <summary>Groups of active (non-merged) creators sharing a normalised name or alias.</summary>
    public async Task<List<MergeSuggestion>> GetMergeSuggestionsAsync(CancellationToken ct = default)
    {
        var creators = await db.Creators.AsNoTracking()
            .Where(c => c.MergedIntoId == null)
            .Select(c => new { c.Id, c.Name, c.Source, c.ModelCount })
            .ToListAsync(ct);
        var activeIds = creators.Select(c => c.Id).ToHashSet();
        var aliases = await db.CreatorAliases.AsNoTracking()
            .Select(a => new { a.CreatorId, a.NormalizedAlias })
            .ToListAsync(ct);

        var keys = creators.Select(c => (c.Id, Key: SourceCatalog.NormalizeName(c.Name)))
            .Concat(aliases.Where(a => activeIds.Contains(a.CreatorId)).Select(a => (Id: a.CreatorId, Key: a.NormalizedAlias)))
            .Where(k => k.Key.Length > 0)
            .Distinct();

        var byId = creators.ToDictionary(c => c.Id);
        return keys.GroupBy(k => k.Key)
            .Select(g => g.Select(k => k.Id).Distinct().ToList())
            .Where(ids => ids.Count > 1)
            .Select(ids => ids.Select(id => byId[id])
                .OrderByDescending(c => c.ModelCount).ThenBy(c => c.Name).ToList())
            .GroupBy(list => string.Join(',', list.Select(c => c.Id).OrderBy(i => i))) // dedupe identical groups
            .Select(g => g.First())
            .Select(list => new MergeSuggestion(
                SourceCatalog.NormalizeName(list[0].Name),
                list[0].Id,
                list.Select(c => new SuggestedCreator(c.Id, c.Name, c.Source.Slug(), c.ModelCount)).ToList()))
            .OrderByDescending(s => s.Creators.Sum(c => c.ModelCount))
            .ToList();
    }

    public async Task<CreatorMerge> MergeAsync(Guid survivorId, Guid mergedId, string? reason, string? mergedBy, CancellationToken ct = default)
    {
        if (survivorId == mergedId) throw new MergeException("Cannot merge a creator into itself");

        var survivor = await db.Creators.FirstOrDefaultAsync(c => c.Id == survivorId, ct)
            ?? throw new MergeException("Survivor creator not found", 404);
        var merged = await db.Creators.FirstOrDefaultAsync(c => c.Id == mergedId, ct)
            ?? throw new MergeException("Merged creator not found", 404);
        if (survivor.MergedIntoId is not null) throw new MergeException("Survivor is itself merged; unmerge it first", 409);
        if (merged.MergedIntoId is not null) throw new MergeException("Creator is already merged", 409);
        if (await db.Creators.AnyAsync(c => c.MergedIntoId == mergedId, ct))
            throw new MergeException("Other creators are merged into this creator; unmerge them first", 409);

        var models = await db.Models.Where(m => m.CreatorId == mergedId).ToListAsync(ct);
        var profiles = await db.CreatorProfiles.Where(p => p.CreatorId == mergedId).ToListAsync(ct);
        var mergedAliases = await db.CreatorAliases.Where(a => a.CreatorId == mergedId).ToListAsync(ct);
        var survivorAliasKeys = (await db.CreatorAliases.Where(a => a.CreatorId == survivorId)
            .Select(a => a.NormalizedAlias).ToListAsync(ct)).ToHashSet();

        foreach (var m in models) { m.CreatorId = survivorId; m.UpdatedAt = DateTime.UtcNow; }
        foreach (var p in profiles) { p.CreatorId = survivorId; p.UpdatedAt = DateTime.UtcNow; }
        var movedAliases = mergedAliases.Where(a => survivorAliasKeys.Add(a.NormalizedAlias)).ToList();
        foreach (var a in movedAliases) a.CreatorId = survivorId;

        // Remember the merged creator's name as an alias of the survivor.
        Guid? createdAliasId = null;
        var mergedKey = SourceCatalog.NormalizeName(merged.Name);
        if (mergedKey.Length > 0 && survivorAliasKeys.Add(mergedKey))
        {
            var alias = new CreatorAlias
            {
                Id = Guid.NewGuid(), CreatorId = survivorId, Alias = merged.Name,
                NormalizedAlias = mergedKey, Kind = "merged",
            };
            db.CreatorAliases.Add(alias);
            createdAliasId = alias.Id;
        }

        merged.MergedIntoId = survivorId;
        merged.ModelCount = 0;
        merged.UpdatedAt = DateTime.UtcNow;
        survivor.ModelCount += models.Count;
        survivor.UpdatedAt = DateTime.UtcNow;

        var record = new CreatorMerge
        {
            Id = Guid.NewGuid(),
            SurvivorId = survivorId,
            MergedId = mergedId,
            Reason = reason,
            MergedBy = mergedBy,
            Snapshot = JsonSerializer.Serialize(new MergeSnapshot(
                models.Select(m => m.Id).ToList(),
                profiles.Select(p => p.Id).ToList(),
                movedAliases.Select(a => a.Id).ToList(),
                createdAliasId)),
        };
        db.CreatorMerges.Add(record);
        await db.SaveChangesAsync(ct);
        return record;
    }

    public async Task<CreatorMerge> UnmergeAsync(Guid mergeId, CancellationToken ct = default)
    {
        var record = await db.CreatorMerges.FirstOrDefaultAsync(m => m.Id == mergeId, ct)
            ?? throw new MergeException("Merge not found", 404);
        if (record.UnmergedAt is not null) throw new MergeException("Merge already reverted", 409);

        var snap = JsonSerializer.Deserialize<MergeSnapshot>(record.Snapshot)
            ?? new MergeSnapshot([], [], [], null);
        var survivor = await db.Creators.FirstOrDefaultAsync(c => c.Id == record.SurvivorId, ct);
        var merged = await db.Creators.FirstOrDefaultAsync(c => c.Id == record.MergedId, ct)
            ?? throw new MergeException("Merged creator no longer exists; cannot unmerge", 409);

        // Only move rows that are still attached to the survivor (later edits win).
        var models = await db.Models.Where(m => snap.ModelIds.Contains(m.Id) && m.CreatorId == record.SurvivorId).ToListAsync(ct);
        var profiles = await db.CreatorProfiles.Where(p => snap.ProfileIds.Contains(p.Id) && p.CreatorId == record.SurvivorId).ToListAsync(ct);
        var aliases = await db.CreatorAliases.Where(a => snap.AliasIds.Contains(a.Id) && a.CreatorId == record.SurvivorId).ToListAsync(ct);

        foreach (var m in models) { m.CreatorId = record.MergedId; m.UpdatedAt = DateTime.UtcNow; }
        foreach (var p in profiles) { p.CreatorId = record.MergedId; p.UpdatedAt = DateTime.UtcNow; }
        foreach (var a in aliases) a.CreatorId = record.MergedId;

        if (snap.CreatedAliasId is { } aliasId)
        {
            var created = await db.CreatorAliases.FirstOrDefaultAsync(a => a.Id == aliasId, ct);
            if (created is not null) db.CreatorAliases.Remove(created);
        }

        merged.MergedIntoId = null;
        merged.ModelCount += models.Count;
        merged.UpdatedAt = DateTime.UtcNow;
        if (survivor is not null)
        {
            survivor.ModelCount = Math.Max(0, survivor.ModelCount - models.Count);
            survivor.UpdatedAt = DateTime.UtcNow;
        }

        record.UnmergedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return record;
    }
}
