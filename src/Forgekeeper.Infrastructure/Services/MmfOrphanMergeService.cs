using System.Text.Json;
using System.Text.RegularExpressions;
using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// Repairs MMF "metadata orphans" (#63) and wrong source assignment (#67).
///
/// An orphan is a zero-file model row with an MMF identity whose BasePath lives under a dot-directory
/// (e.g. <c>sources/mmf/.trash-20261009/…</c>). Older MMF syncs wrote metadata-only folders beside an
/// existing local folder; a later duplicate cleanup quarantined those folders but left the rows (and with
/// them the MMF tags, URL, description) detached from the file-bearing twin.
///
/// The merge pairs each orphan with a file-bearing row by (creator, normalized name). Only strict 1:1
/// pairs are merged; everything else is reported as ambiguous/unmatched.
/// </summary>
public class MmfOrphanMergeService(ForgeDbContext db, IMetadataService metadata, ILogger<MmfOrphanMergeService> logger)
{
    private static readonly Regex MmfId = new(@"^(object-)?\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NonWord = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    public record PairInfo(Guid OrphanId, Guid KeepId, string Creator, string Name, string OrphanPath, string KeepPath,
        string? SourceId, int OrphanTags, int KeepTags);

    public record MergeReport(
        bool Applied, int Orphans, int Pairs, int Ambiguous, int Unmatched, int Merged,
        List<PairInfo> PairList, List<string> AmbiguousList, List<string> UnmatchedList);

    public record SourceBackfillReport(bool Applied, int MmfBefore, int ToManual, List<string> Sample);

    /// <summary>True when the identity looks like a real MyMiniFactory object (numeric/object-NNN id or MMF URL).</summary>
    public static bool IsMmfIdentity(string? sourceId, string? sourceUrl) =>
        (!string.IsNullOrWhiteSpace(sourceId) && MmfId.IsMatch(sourceId.Trim()))
        || (sourceUrl?.Contains("myminifactory.com", StringComparison.OrdinalIgnoreCase) ?? false);

    public static string NormalizeName(string? name) =>
        NonWord.Replace((name ?? string.Empty).ToLowerInvariant(), string.Empty);

    /// <summary>True when any path segment (after the library root) starts with '.', e.g. a quarantine dir.</summary>
    public static bool IsUnderDotDir(string? path) =>
        !string.IsNullOrEmpty(path)
        && path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(seg => seg.StartsWith('.'));

    public async Task<MergeReport> MergeOrphansAsync(bool apply, CancellationToken ct = default)
    {
        var rows = await db.Models
            .Include(m => m.Tags)
            .Include(m => m.Creator)
            .ToListAsync(ct);

        var orphans = rows.Where(m => m.FileCount == 0 && IsUnderDotDir(m.BasePath)
                                      && IsMmfIdentity(m.SourceId, m.SourceUrl)).ToList();
        var keepers = rows.Where(m => m.FileCount > 0 && !IsUnderDotDir(m.BasePath)).ToList();

        var keeperIndex = keepers
            .GroupBy(m => (m.CreatorId, NormalizeName(m.Name)))
            .ToDictionary(g => g.Key, g => g.ToList());
        var orphanIndex = orphans
            .GroupBy(m => (m.CreatorId, NormalizeName(m.Name)))
            .ToDictionary(g => g.Key, g => g.ToList());

        var pairs = new List<(Model3D Orphan, Model3D Keep)>();
        var ambiguous = new List<string>();
        var unmatched = new List<string>();

        foreach (var o in orphans)
        {
            var key = (o.CreatorId, NormalizeName(o.Name));
            if (!keeperIndex.TryGetValue(key, out var ks)) { unmatched.Add($"{o.Creator?.Name} / {o.Name}"); continue; }
            if (ks.Count != 1 || orphanIndex[key].Count != 1)
            {
                ambiguous.Add($"{o.Creator?.Name} / {o.Name} (orphans={orphanIndex[key].Count}, files={ks.Count})");
                continue;
            }
            var k = ks[0];
            // Never overwrite a keeper that already has a different real MMF identity.
            if (IsMmfIdentity(k.SourceId, null) && !string.Equals(k.SourceId, o.SourceId, StringComparison.OrdinalIgnoreCase))
            {
                ambiguous.Add($"{o.Creator?.Name} / {o.Name} (keeper has different MMF id {k.SourceId} vs {o.SourceId})");
                continue;
            }
            pairs.Add((o, k));
        }

        var merged = 0;
        if (apply)
        {
            foreach (var (o, k) in pairs)
            {
                await MergeOneAsync(o, k, ct);
                merged++;
                if (merged % 200 == 0) await db.SaveChangesAsync(ct);
            }
            await db.SaveChangesAsync(ct);
            logger.LogInformation("MMF orphan merge applied: {Merged} pairs merged", merged);
        }

        return new MergeReport(apply, orphans.Count, pairs.Count, ambiguous.Count, unmatched.Count, merged,
            pairs.Select(p => new PairInfo(p.Orphan.Id, p.Keep.Id, p.Keep.Creator?.Name ?? "", p.Keep.Name,
                p.Orphan.BasePath, p.Keep.BasePath, p.Orphan.SourceId, p.Orphan.Tags.Count, p.Keep.Tags.Count)).ToList(),
            ambiguous, unmatched);
    }

    private async Task MergeOneAsync(Model3D o, Model3D k, CancellationToken ct)
    {
        // 1. metadata.json on disk — the scanner re-derives SourceId/Url/Description from it on every rescan.
        var orphanMeta = Directory.Exists(o.BasePath) ? await metadata.ReadAsync(o.BasePath, ct) : null;
        var keepMeta = Directory.Exists(k.BasePath) ? await metadata.ReadAsync(k.BasePath, ct) : null;
        if (Directory.Exists(k.BasePath))
        {
            var m = keepMeta ?? new SourceMetadata { MetadataVersion = 1, Name = k.Name };
            m.Source = "mmf";
            m.ExternalId = o.SourceId!;
            m.ExternalUrl = orphanMeta?.ExternalUrl ?? o.SourceUrl ?? m.ExternalUrl;
            if (string.IsNullOrWhiteSpace(m.Description)) m.Description = orphanMeta?.Description ?? o.Description;
            m.Type ??= orphanMeta?.Type;
            var tags = (m.Tags ?? []).Concat(orphanMeta?.Tags ?? []).Concat(o.Tags.Select(t => t.Name))
                .Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            m.Tags = tags;
            m.Creator ??= orphanMeta?.Creator;
            m.Dates ??= orphanMeta?.Dates;
            m.Acquisition ??= orphanMeta?.Acquisition;
            m.Acquisitions ??= orphanMeta?.Acquisitions;
            m.LibraryAddedAt ??= orphanMeta?.LibraryAddedAt;
            if (m.Images == null || m.Images.Count == 0) m.Images = orphanMeta?.Images;
            m.License ??= orphanMeta?.License;
            m.Collection ??= orphanMeta?.Collection;
            m.Rating ??= orphanMeta?.Rating;
            m.Extra ??= orphanMeta?.Extra;
            await metadata.WriteAsync(k.BasePath, m, ct);
        }

        // 2. DB row
        k.SourceId = o.SourceId;
        k.SourceUrl = o.SourceUrl ?? orphanMeta?.ExternalUrl ?? k.SourceUrl;
        k.Source = SourceType.Mmf;
        if (string.IsNullOrWhiteSpace(k.Description)) k.Description = o.Description;
        k.Extra ??= o.Extra;
        k.LicenseType ??= o.LicenseType;
        k.CollectionName ??= o.CollectionName;
        k.Category ??= o.Category;
        k.GameSystem ??= o.GameSystem;
        k.Scale ??= o.Scale;
        k.ExternalCreatedAt ??= o.ExternalCreatedAt;
        k.ExternalUpdatedAt ??= o.ExternalUpdatedAt;
        k.PublishedAt ??= o.PublishedAt;
        if (k.PreviewImages.Count == 0 && o.PreviewImages.Count > 0) k.PreviewImages = [.. o.PreviewImages];
        k.ThumbnailPath ??= o.ThumbnailPath;
        foreach (var t in o.Tags.ToList())
            if (!k.Tags.Any(x => x.Id == t.Id)) k.Tags.Add(t);
        k.UpdatedAt = DateTime.UtcNow;

        db.Models.Remove(o);
    }

    /// <summary>#67: Source=Mmf only when the row has a real MMF identity; otherwise Manual (local scan).</summary>
    public async Task<SourceBackfillReport> BackfillSourceAsync(bool apply, CancellationToken ct = default)
    {
        var mmf = await db.Models.Where(m => m.Source == SourceType.Mmf).ToListAsync(ct);
        var wrong = mmf.Where(m => !IsMmfIdentity(m.SourceId, m.SourceUrl)).ToList();
        if (apply)
        {
            foreach (var m in wrong) m.Source = SourceType.Manual;
            await db.SaveChangesAsync(ct);
        }
        return new SourceBackfillReport(apply, mmf.Count, wrong.Count, wrong.Take(20).Select(m => m.BasePath).ToList());
    }

    public static async Task<string> WriteReportAsync(string dir, string name, object report, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{name}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct);
        return path;
    }
}
