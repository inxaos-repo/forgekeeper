using System.Text.RegularExpressions;
using Forgekeeper.Core.Enums;

namespace Forgekeeper.Core.Services;

/// <summary>Evidence available for one model directory when deciding its source.</summary>
public sealed record SourceEvidence
{
    /// <summary>Folder slug under /library/sources/ (e.g. "mmf", "patreon").</summary>
    public string? FolderSlug { get; init; }

    /// <summary>The model folder name (last path segment).</summary>
    public string? FolderName { get; init; }

    /// <summary>metadata.json "source" field.</summary>
    public string? MetadataSource { get; init; }

    /// <summary>metadata.json "metadataVersion". v1 files were written by Forgekeeper's
    /// own writeback, which stamps the folder's source and invents a GUID externalId, so they
    /// are not independent evidence.</summary>
    public int? MetadataVersion { get; init; }

    public string? MetadataExternalId { get; init; }
    public string? MetadataExternalUrl { get; init; }

    /// <summary>Id the source adapter parsed from the folder name (e.g. "Name - 12345").</summary>
    public string? ParsedSourceId { get; init; }

    /// <summary>Plugin-supplied data already on the row (SourceId / SourceUrl from a scraper).</summary>
    public string? PluginSourceId { get; init; }
    public string? PluginSourceUrl { get; init; }
}

public sealed record SourceDecision(SourceType Source, string? SourceKey, string Detection, string Reason);

/// <summary>
/// Decides which platform a model came from. Order:
///   1. folder convention — sources/&lt;slug&gt;/ wins for any slug that is not a catch-all;
///   2. metadata files — metadata.json externalUrl domain, or a plugin-written (v2+) "source";
///   3. plugin data — SourceId/SourceUrl recorded by a scraper plugin, or an MMF-shaped id;
///   4. otherwise Unknown (when in a catch-all folder) — never silently "Mmf".
/// Pure and deterministic so the scanner and the re-categorize command agree.
/// </summary>
public static partial class SourceDetector
{
    /// <summary>Folders that historically hold everything, so the folder alone proves nothing.</summary>
    public static readonly IReadOnlySet<string> CatchAllSlugs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mmf", "unknown", "unsorted" };

    public static SourceType? FromSlug(string? slug) => slug?.Trim().ToLowerInvariant() switch
    {
        "mmf" or "myminifactory" => SourceType.Mmf,
        "patreon" => SourceType.Patreon,
        "thangs" => SourceType.Thangs,
        "cults" or "cults3d" => SourceType.Cults3d,
        "thingiverse" => SourceType.Thingiverse,
        "gumroad" => SourceType.Gumroad,
        "manual" or "local" => SourceType.Manual,
        "unknown" => SourceType.Unknown,
        _ => null,
    };

    public static SourceType? FromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return null;
        var h = u.Host.ToLowerInvariant();
        bool Is(string d) => h == d || h.EndsWith("." + d);
        if (Is("myminifactory.com")) return SourceType.Mmf;
        if (Is("patreon.com")) return SourceType.Patreon;
        if (Is("cults3d.com")) return SourceType.Cults3d;
        if (Is("thangs.com")) return SourceType.Thangs;
        if (Is("gumroad.com")) return SourceType.Gumroad;
        if (Is("thingiverse.com")) return SourceType.Thingiverse;
        return null;
    }

    /// <summary>MMF object ids are positive integers without leading zeros, optionally "object-".
    /// "001" (Google Drive export suffix) and GUIDs are rejected.</summary>
    public static bool IsMmfId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && MmfIdRegex().IsMatch(id.Trim());

    /// <summary>Folder names like "Welcome Pack-20240109T045122Z-001" are Google Drive zip
    /// exports — typically Patreon/Gumroad deliveries, never MMF downloads.</summary>
    public static bool IsDriveExport(string? folderName) =>
        !string.IsNullOrEmpty(folderName) && DriveExportRegex().IsMatch(folderName);

    public static string? NormalizeId(SourceType source, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        id = id.Trim();
        if (Guid.TryParse(id, out _)) return null;            // writeback-invented, not a platform id
        if (source == SourceType.Mmf)
        {
            if (!IsMmfId(id)) return null;
            return id.StartsWith("object-", StringComparison.OrdinalIgnoreCase) ? id[7..] : id;
        }
        if (source is SourceType.Manual or SourceType.Unknown) return null;
        if (id.Length < 2 || id.TrimStart('0').Length == 0) return null;
        return id;
    }

    public static string? BuildKey(SourceType source, string? id)
    {
        var n = NormalizeId(source, id);
        return n == null ? null : $"{source.ToString().ToLowerInvariant()}:{n}";
    }

    public static SourceDecision Detect(SourceEvidence e)
    {
        var folderType = FromSlug(e.FolderSlug);
        var catchAll = e.FolderSlug == null || CatchAllSlugs.Contains(e.FolderSlug);
        var bestId = FirstPlausible(e.PluginSourceId, e.MetadataExternalId, e.ParsedSourceId);

        // 1. Folder convention
        if (folderType is { } ft && !catchAll)
            return new(ft, BuildKey(ft, bestId), "folder", $"sources/{e.FolderSlug}/");

        // 2. Metadata files
        if (FromUrl(e.MetadataExternalUrl) is { } mu)
            return new(mu, BuildKey(mu, e.MetadataExternalId ?? bestId), "metadata",
                $"metadata.json externalUrl {e.MetadataExternalUrl}");
        if ((e.MetadataVersion ?? 0) >= 2 && FromSlug(e.MetadataSource) is { } ms
            && ms is not SourceType.Manual and not SourceType.Unknown
            && (ms != SourceType.Mmf || IsMmfId(e.MetadataExternalId)))
            return new(ms, BuildKey(ms, e.MetadataExternalId), "metadata",
                $"metadata.json v{e.MetadataVersion} source={e.MetadataSource}");

        // 3. Plugin data / folder-name id
        if (FromUrl(e.PluginSourceUrl) is { } pu)
            return new(pu, BuildKey(pu, bestId), "plugin", $"plugin url {e.PluginSourceUrl}");
        if (!IsDriveExport(e.FolderName))
        {
            var mmfId = new[] { e.PluginSourceId, e.MetadataExternalId, e.ParsedSourceId }.FirstOrDefault(IsMmfId);
            if (mmfId != null && folderType is SourceType.Mmf or null)
                return new(SourceType.Mmf, BuildKey(SourceType.Mmf, mmfId), "plugin", $"MMF id {mmfId}");
        }

        // 4. No evidence
        var why = IsDriveExport(e.FolderName) ? "Google Drive export folder, platform unknown"
            : $"catch-all folder sources/{e.FolderSlug ?? "?"}/ with no platform id or url";
        return new(SourceType.Unknown, null, "folder", why);
    }

    private static string? FirstPlausible(params string?[] ids) =>
        ids.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i) && !Guid.TryParse(i, out _));

    [GeneratedRegex(@"^(object-)?[1-9]\d*$", RegexOptions.IgnoreCase)]
    private static partial Regex MmfIdRegex();

    [GeneratedRegex(@"-\d{8}T\d{6}Z-\d{3}$")]
    private static partial Regex DriveExportRegex();
}
