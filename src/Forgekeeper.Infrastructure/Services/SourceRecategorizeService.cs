using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Core.Models;
using Forgekeeper.Core.Services;
using Forgekeeper.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Infrastructure.Services;

public sealed record SourceChange(Guid ModelId, string Path, string From, string To, string Detection, string Reason);

public sealed class SourceRecategorizeReport
{
    public bool Applied { get; set; }
    public bool ReadMetadataFiles { get; set; }
    public int ModelsScanned { get; set; }
    public int MissingDirs { get; set; }
    public Dictionary<string, int> Transitions { get; set; } = new();     // "Manual->Unknown": n
    public Dictionary<string, int> ResultingCounts { get; set; } = new(); // by SourceType
    public int CreatorMoves { get; set; }
    public int CreatorIdentitiesCreated { get; set; }
    public int CanonicalLinks { get; set; }
    public int SourceKeysSet { get; set; }
    public List<string> SourceKeyConflicts { get; set; } = new();
    public List<SourceChange> Examples { get; set; } = new();
    public List<string> CanonicalLinkExamples { get; set; } = new();
}

/// <summary>
/// Re-categorizes every model with <see cref="SourceDetector"/> and splits creators into
/// per-source identities linked to a canonical creator. DRY-RUN unless apply=true.
/// Never moves, renames or writes files; DB only.
/// </summary>
public class SourceRecategorizeService(ForgeDbContext db, IMetadataService metadata, ILogger<SourceRecategorizeService> logger)
{
    private const int MaxExamples = 25;

    public static string? SlugFromPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.FindIndex(segs, s => s.Equals("sources", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < segs.Length ? segs[i + 1].ToLowerInvariant() : null;
    }

    public async Task<SourceRecategorizeReport> RunAsync(bool apply, bool readMetadataFiles = true, CancellationToken ct = default)
    {
        var report = new SourceRecategorizeReport { Applied = apply, ReadMetadataFiles = readMetadataFiles };
        var creators = await db.Creators.ToListAsync(ct);
        var byId = creators.ToDictionary(c => c.Id);
        var models = await db.Models.ToListAsync(ct);
        report.ModelsScanned = models.Count;

        // 1. Canonical links between existing identities with the same normalized name.
        foreach (var grp in creators.GroupBy(c => CreatorIdentityResolver.Key(c.Name)).Where(g => g.Key.Length > 0 && g.Count() > 1))
        {
            var root = grp.OrderBy(c => c.CanonicalCreatorId == null ? 0 : 1).ThenBy(c => c.CreatedAt).ThenBy(c => c.Id).First();
            root.CanonicalCreatorId = null;
            foreach (var c in grp.Where(c => c.Id != root.Id && c.CanonicalCreatorId != root.Id))
            {
                report.CanonicalLinks++;
                if (report.CanonicalLinkExamples.Count < MaxExamples)
                    report.CanonicalLinkExamples.Add($"{c.Name} [{c.Source}] -> {root.Name} [{root.Source}]");
                c.CanonicalCreatorId = root.Id;
            }
        }

        var keysTaken = models.Where(m => m.SourceKey != null).ToDictionary(m => m.SourceKey!, m => m.Id);
        var identityCache = creators.ToDictionary(c => (c.Name, c.Source));

        foreach (var m in models)
        {
            ct.ThrowIfCancellationRequested();
            if (m.SourceDetection == "override") { Count(report.ResultingCounts, m.Source.ToString()); continue; }

            SourceMetadata? meta = null;
            if (readMetadataFiles)
            {
                if (Directory.Exists(m.BasePath)) meta = await metadata.ReadAsync(m.BasePath, ct);
                else report.MissingDirs++;
            }

            var d = SourceDetector.Detect(new SourceEvidence
            {
                FolderSlug = SlugFromPath(m.BasePath),
                FolderName = Path.GetFileName(m.BasePath.TrimEnd('/')),
                MetadataSource = meta?.Source,
                MetadataVersion = meta?.MetadataVersion,
                MetadataExternalId = meta?.ExternalId,
                MetadataExternalUrl = meta?.ExternalUrl,
                PluginSourceId = m.SourceId,
                PluginSourceUrl = m.SourceUrl,
            });

            if (d.Source != m.Source)
            {
                Count(report.Transitions, $"{m.Source}->{d.Source}");
                if (report.Examples.Count < MaxExamples)
                    report.Examples.Add(new(m.Id, m.BasePath, m.Source.ToString(), d.Source.ToString(), d.Detection, d.Reason));
            }
            Count(report.ResultingCounts, d.Source.ToString());

            // Creator identity for the decided source.
            if (byId.TryGetValue(m.CreatorId, out var cur) && cur.Source != d.Source)
            {
                if (!identityCache.TryGetValue((cur.Name, d.Source), out var target))
                {
                    target = new Creator
                    {
                        Id = Guid.NewGuid(), Name = cur.Name, Source = d.Source,
                        CanonicalCreatorId = cur.CanonicalCreatorId ?? cur.Id,
                    };
                    identityCache[(cur.Name, d.Source)] = target;
                    byId[target.Id] = target;
                    report.CreatorIdentitiesCreated++;
                    if (apply) db.Creators.Add(target);
                }
                report.CreatorMoves++;
                if (apply) m.CreatorId = target.Id;
            }

            if (d.SourceKey != null && d.SourceKey != m.SourceKey)
            {
                if (keysTaken.TryGetValue(d.SourceKey, out var owner) && owner != m.Id)
                {
                    if (report.SourceKeyConflicts.Count < 200)
                        report.SourceKeyConflicts.Add($"{d.SourceKey}: {m.BasePath} (kept on {owner})");
                }
                else
                {
                    keysTaken[d.SourceKey] = m.Id;
                    report.SourceKeysSet++;
                    if (apply) m.SourceKey = d.SourceKey;
                }
            }

            if (apply)
            {
                m.Source = d.Source;
                m.SourceDetection = d.Detection;
            }
        }

        if (apply)
        {
            await db.SaveChangesAsync(ct);
            await RecountAsync(ct);
            logger.LogInformation("Source re-categorize applied: {Transitions}", string.Join(", ", report.Transitions.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        else
        {
            // Dry run: discard canonical-link mutations made on tracked entities.
            db.ChangeTracker.Clear();
        }
        return report;
    }

    private async Task RecountAsync(CancellationToken ct)
    {
        var counts = await db.Models.GroupBy(m => m.CreatorId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var map = counts.ToDictionary(x => x.Key, x => x.N);
        foreach (var c in await db.Creators.ToListAsync(ct))
            c.ModelCount = map.GetValueOrDefault(c.Id);
        await db.SaveChangesAsync(ct);
    }

    private static void Count(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;

    public static string Format(SourceRecategorizeReport r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(r.Applied ? "=== Source re-categorize: APPLIED ===" : "=== Source re-categorize: DRY RUN (nothing written; pass --apply) ===");
        sb.AppendLine($"models scanned: {r.ModelsScanned}  (metadata files read: {r.ReadMetadataFiles}, missing dirs: {r.MissingDirs})");
        sb.AppendLine("transitions:");
        foreach (var kv in r.Transitions.OrderByDescending(kv => kv.Value)) sb.AppendLine($"  {kv.Key,-24} {kv.Value}");
        sb.AppendLine("resulting counts:");
        foreach (var kv in r.ResultingCounts.OrderByDescending(kv => kv.Value)) sb.AppendLine($"  {kv.Key,-24} {kv.Value}");
        sb.AppendLine($"creator moves: {r.CreatorMoves}  new per-source identities: {r.CreatorIdentitiesCreated}  canonical links: {r.CanonicalLinks}");
        sb.AppendLine($"source keys set: {r.SourceKeysSet}  conflicts: {r.SourceKeyConflicts.Count}");
        foreach (var c in r.SourceKeyConflicts.Take(20)) sb.AppendLine($"  conflict {c}");
        foreach (var c in r.CanonicalLinkExamples) sb.AppendLine($"  link {c}");
        foreach (var e in r.Examples) sb.AppendLine($"  {e.From}->{e.To} [{e.Detection}] {e.Path} — {e.Reason}");
        return sb.ToString();
    }
}
