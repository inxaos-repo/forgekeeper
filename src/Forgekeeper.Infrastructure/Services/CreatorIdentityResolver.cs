using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// Creators are per-source identities (unique on Name+Source). This resolver finds or
/// creates the identity for a given source and links it to one canonical creator,
/// matched by normalized name ("Owen Harrison" == "owen harrison" == "Owen-Harrison").
/// </summary>
public static class CreatorIdentityResolver
{
    public static string Key(string? name) => MmfOrphanMergeService.NormalizeName(name);

    /// <summary>Find-or-create the (name, source) identity. New identities are linked to an
    /// existing canonical creator with the same normalized name. Does not SaveChanges.</summary>
    public static async Task<Creator> ResolveAsync(
        ForgeDbContext db, string name, SourceType source,
        string? profileUrl = null, string? externalId = null, string? avatarUrl = null,
        CancellationToken ct = default)
    {
        var existing = db.Creators.Local.FirstOrDefault(c => c.Name == name && c.Source == source)
            ?? await db.Creators.FirstOrDefaultAsync(c => c.Name == name && c.Source == source, ct);
        if (existing != null) return existing;

        var creator = new Creator
        {
            Id = Guid.NewGuid(),
            Name = name,
            Source = source,
            SourceUrl = profileUrl,
            ExternalId = externalId,
            AvatarUrl = avatarUrl,
        };
        var canonical = await FindCanonicalAsync(db, name, ct);
        if (canonical != null) creator.CanonicalCreatorId = canonical.Id;
        db.Creators.Add(creator);
        return creator;
    }

    /// <summary>Canonical row for a normalized name: the oldest root (CanonicalCreatorId == null).</summary>
    public static async Task<Creator?> FindCanonicalAsync(ForgeDbContext db, string name, CancellationToken ct = default)
    {
        var key = Key(name);
        if (key.Length == 0) return null;
        // Prefilter in SQL by case-insensitive prefix-free match on the first char, finish in memory:
        // creator tables are small (hundreds), and NormalizeName isn't translatable.
        var roots = await db.Creators.Where(c => c.CanonicalCreatorId == null).ToListAsync(ct);
        var all = roots.Concat(db.Creators.Local.Where(c => c.CanonicalCreatorId == null)).Distinct();
        return all.Where(c => Key(c.Name) == key)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .FirstOrDefault();
    }

    /// <summary>All identity ids (canonical + aliases) for the canonical root of <paramref name="creatorId"/>.</summary>
    public static async Task<List<Guid>> IdentityIdsAsync(ForgeDbContext db, Guid creatorId, CancellationToken ct = default)
    {
        var c = await db.Creators.AsNoTracking().FirstOrDefaultAsync(x => x.Id == creatorId, ct);
        if (c == null) return [creatorId];
        var root = c.CanonicalCreatorId ?? c.Id;
        return await db.Creators.AsNoTracking()
            .Where(x => x.Id == root || x.CanonicalCreatorId == root)
            .Select(x => x.Id).ToListAsync(ct);
    }
}
