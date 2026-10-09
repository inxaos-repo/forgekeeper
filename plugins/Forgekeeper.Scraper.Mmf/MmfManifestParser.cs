using System.Globalization;
using System.Text.Json;
using Forgekeeper.PluginSdk;

namespace Forgekeeper.Scraper.Mmf;

/// <summary>Counts reported after parsing + deduplicating an MMF manifest.</summary>
public sealed record MmfManifestStats(int Rows, int DistinctRows, int UniqueItems, int MultiSourceItems, int RowsWithoutCreator);

/// <summary>One parsed manifest row before dedupe (internal shape).</summary>
internal sealed record MmfManifestRow(
    string Id,
    string Name,
    string? Type,
    string? CreatorName,
    string? CreatorId,
    string? CreatorUsername,
    string? Source,
    string? Release,
    string? CampaignId,
    string? BundleId,
    string? OrderReference,
    IReadOnlyList<string> Tags,
    DateTime? LibraryAddedAt,
    DateTime? UpdatedAt,
    string RawJson);

/// <summary>
/// Parses the MyMiniFactory library manifest (objectPreviews / data-library export).
///
/// Live row schema (Phase 0): originalId, id, type, name, tags, createdAt, updatedAt,
/// publishedAt, source, creatorUsername, creatorName, creatorId, creatorAvatar,
/// libraryAddedAt, + optional release, order, campaignId, bundleId.
/// Older exports used a nested designer {id,name,username} object — kept as a fallback.
///
/// Identity is always the full prefixed id ("object-3147" ≠ "bundle-3147").
/// The same id appears in many rows (exact repeats and one row per acquisition source);
/// rows are merged into one <see cref="ScrapedModel"/> with every acquisition.
/// </summary>
public static class MmfManifestParser
{
    public static async Task<(IReadOnlyList<ScrapedModel> Models, MmfManifestStats Stats)> ParseAsync(
        Stream manifestStream, CancellationToken ct = default)
    {
        using var doc = await JsonDocument.ParseAsync(manifestStream, cancellationToken: ct);
        return Parse(doc.RootElement);
    }

    public static (IReadOnlyList<ScrapedModel> Models, MmfManifestStats Stats) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement);
    }

    public static (IReadOnlyList<ScrapedModel> Models, MmfManifestStats Stats) Parse(JsonElement root)
    {
        var rows = ReadRows(root).ToList();
        var distinct = rows.Select(r => r.RawJson).Distinct(StringComparer.Ordinal).Count();
        var models = Merge(rows);
        var stats = new MmfManifestStats(
            Rows: rows.Count,
            DistinctRows: distinct,
            UniqueItems: models.Count,
            MultiSourceItems: models.Count(m => m.Acquisitions.Select(a => a.Source).Distinct().Count() > 1),
            RowsWithoutCreator: rows.Count(r => string.IsNullOrWhiteSpace(r.CreatorName)));
        return (models, stats);
    }

    /// <summary>Normalize any MMF id to its prefixed form. Bare numbers are treated as objects.</summary>
    public static string NormalizeId(string id, string? type = null)
    {
        id = id.Trim();
        if (id.StartsWith("object-", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("bundle-", StringComparison.OrdinalIgnoreCase))
            return id.ToLowerInvariant();
        var prefix = string.Equals(type, "bundle", StringComparison.OrdinalIgnoreCase) ? "bundle" : "object";
        return $"{prefix}-{id}";
    }

    /// <summary>Numeric part of a prefixed id ("object-123" → "123").</summary>
    public static string NumericId(string id)
    {
        var dash = id.LastIndexOf('-');
        return dash >= 0 ? id[(dash + 1)..] : id;
    }

    /// <summary>
    /// Map an MMF <c>source</c> to a Forgekeeper AcquisitionMethod enum name.
    /// Values must match Forgekeeper.Core.Enums.AcquisitionMethod (string-persisted).
    /// </summary>
    public static string MapAcquisitionMethod(string? source, string? release = null) =>
        (source ?? "").Trim().ToUpperInvariant() switch
        {
            "PURCHASE" => "Purchase",
            "TRIBE" => "Tribe",
            "FRONTIER" => "Campaign",
            "USER_GROUP" => "UserGroup",
            "THE_ADVENTURE" => "Subscription",
            "DOWNLOAD" => "Free",
            "GIFT" => "Gift",
            _ => "Unknown",
        };

    // ─── internals ──────────────────────────────────────────────────────────

    private static JsonElement? FindItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "objectPreviews", "items", "objects", "data" })
        {
            if (root.TryGetProperty(key, out var p))
            {
                if (p.ValueKind == JsonValueKind.Array) return p;
                if (p.ValueKind == JsonValueKind.Object) return FindItems(p);
            }
        }
        return null;
    }

    internal static IEnumerable<MmfManifestRow> ReadRows(JsonElement root)
    {
        if (FindItems(root) is not { } items) yield break;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var rawId = Str(item, "id");
            var name = Str(item, "name");
            if (string.IsNullOrWhiteSpace(rawId) || string.IsNullOrWhiteSpace(name)) continue;

            var type = Str(item, "type");
            var id = NormalizeId(rawId, type);

            item.TryGetProperty("designer", out var designer);
            bool hasDesigner = designer.ValueKind == JsonValueKind.Object;

            var creatorName = Str(item, "creatorName")
                ?? (hasDesigner ? Str(designer, "name") : null);
            var creatorId = Str(item, "creatorId")
                ?? (hasDesigner ? Str(designer, "id") : null);
            var creatorUsername = Str(item, "creatorUsername")
                ?? (hasDesigner ? Str(designer, "username") : null);
            // Last resort: a username is better than dumping into unknown/.
            if (string.IsNullOrWhiteSpace(creatorName)) creatorName = creatorUsername;

            string? orderRef = null;
            if (item.TryGetProperty("order", out var order))
            {
                orderRef = order.ValueKind == JsonValueKind.Object
                    ? Str(order, "reference") ?? Str(order, "id")
                    : Scalar(order);
            }

            // bundleId on a child points at its parent bundle. The bundle row itself
            // carries its own bundleId — never treat a bundle as its own child.
            string? bundleId = Str(item, "bundleId");
            if (bundleId is not null)
            {
                bundleId = NormalizeId(bundleId, "bundle");
                if (bundleId == id) bundleId = null;
            }

            yield return new MmfManifestRow(
                Id: id,
                Name: name!,
                Type: type,
                CreatorName: creatorName,
                CreatorId: creatorId,
                CreatorUsername: creatorUsername,
                Source: Str(item, "source"),
                Release: Str(item, "release"),
                CampaignId: Str(item, "campaignId"),
                BundleId: bundleId,
                OrderReference: orderRef,
                Tags: ReadTags(item),
                LibraryAddedAt: Date(item, "libraryAddedAt"),
                UpdatedAt: Date(item, "updatedAt"),
                RawJson: item.GetRawText());
        }
    }

    internal static List<ScrapedModel> Merge(IEnumerable<MmfManifestRow> rows)
    {
        var result = new List<ScrapedModel>();
        foreach (var g in rows.GroupBy(r => r.Id, StringComparer.Ordinal))
        {
            var list = g.ToList();
            var first = list[0];

            var acquisitions = list
                .Where(r => r.Source is not null)
                .Select(r => (r.Source, r.Release, r.CampaignId, r.BundleId, r.OrderReference, r.LibraryAddedAt))
                .Distinct()
                .Select(a => new ScrapedAcquisition
                {
                    Source = a.Source!,
                    Method = MapAcquisitionMethod(a.Source, a.Release),
                    AcquiredAt = a.LibraryAddedAt,
                    Reference = a.OrderReference ?? a.CampaignId ?? a.Release,
                })
                .ToList();

            var extra = new Dictionary<string, object>
            {
                ["sources"] = list.Where(r => r.Source is not null).Select(r => r.Source!).Distinct().ToList(),
                ["rows"] = list.Count,
            };
            var releases = list.Select(r => r.Release).Where(x => x is not null).Distinct().ToList();
            if (releases.Count > 0) extra["releases"] = releases;
            var campaigns = list.Select(r => r.CampaignId).Where(x => x is not null).Distinct().ToList();
            if (campaigns.Count > 0) extra["campaignIds"] = campaigns;

            result.Add(new ScrapedModel
            {
                ExternalId = first.Id,
                Name = first.Name,
                Type = first.Type,
                CreatorName = list.Select(r => r.CreatorName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                CreatorId = list.Select(r => r.CreatorId).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                CreatorUsername = list.Select(r => r.CreatorUsername).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                UpdatedAt = list.Max(r => r.UpdatedAt),
                LibraryAddedAt = list.Min(r => r.LibraryAddedAt),
                BundleId = list.Select(r => r.BundleId).FirstOrDefault(b => b is not null),
                Tags = list.SelectMany(r => r.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Acquisitions = acquisitions,
                Extra = extra,
            });
        }
        return result;
    }

    private static IReadOnlyList<string> ReadTags(JsonElement item)
    {
        if (!item.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>();
        foreach (var t in tags.EnumerateArray())
        {
            var s = t.ValueKind == JsonValueKind.Object ? Str(t, "name") ?? Str(t, "slug") : Scalar(t);
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
        }
        return list;
    }

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var p) ? Scalar(p) : null;

    private static string? Scalar(JsonElement p) => p.ValueKind switch
    {
        JsonValueKind.String => string.IsNullOrWhiteSpace(p.GetString()) ? null : p.GetString(),
        JsonValueKind.Number => p.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    private static DateTime? Date(JsonElement e, string prop)
    {
        var s = Str(e, prop);
        return s is not null && DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }
}
