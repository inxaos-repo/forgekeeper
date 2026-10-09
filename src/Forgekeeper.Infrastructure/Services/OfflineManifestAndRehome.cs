using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>Metadata about a manifest JSON that the user uploaded and that is stored on disk.</summary>
public sealed record StoredManifestInfo(string Path, DateTime UploadedAt, int RowCount, long Bytes, string Sha256);

/// <summary>
/// Persists uploaded library manifests (e.g. the MMF objectPreviews JSON) under
/// &lt;sourceDir&gt;/.forgekeeper-reports/manifests/ so a DRY_RUN sync can run fully offline
/// (no login, no FlareSolverr/Playwright, no auth check).
/// </summary>
public static class UploadedManifestStore
{
    public static string ManifestDir(string sourceDir) =>
        Path.Combine(sourceDir, ".forgekeeper-reports", "manifests");

    /// <summary>Write the raw upload plus a .meta.json sidecar. Never overwrites an existing file.</summary>
    public static StoredManifestInfo Save(string sourceDir, string slug, byte[] content, int rowCount, DateTime? now = null)
    {
        var dir = ManifestDir(sourceDir);
        Directory.CreateDirectory(dir);
        var at = now ?? DateTime.UtcNow;
        var baseName = $"manifest-{slug}-{at:yyyyMMdd-HHmmssfff}";
        var path = Path.Combine(dir, baseName + ".json");
        for (var i = 1; File.Exists(path); i++)
            path = Path.Combine(dir, $"{baseName}-{i}.json");

        File.WriteAllBytes(path, content);
        var info = new StoredManifestInfo(path, at, rowCount, content.LongLength,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
        File.WriteAllText(MetaPath(path), JsonSerializer.Serialize(info, JsonOpts));
        return info;
    }

    /// <summary>Latest stored manifest for the slug, or null if none was uploaded.</summary>
    public static StoredManifestInfo? GetLatest(string sourceDir, string slug)
    {
        var dir = ManifestDir(sourceDir);
        if (!Directory.Exists(dir)) return null;
        var latest = Directory.EnumerateFiles(dir, $"manifest-{slug}-*.json")
            .Where(f => !f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .ThenByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .FirstOrDefault();
        if (latest == null) return null;

        try
        {
            var meta = JsonSerializer.Deserialize<StoredManifestInfo>(File.ReadAllText(MetaPath(latest)), JsonOpts);
            if (meta != null) return meta with { Path = latest };
        }
        catch (Exception) { /* missing/corrupt sidecar — fall through */ }
        var fi = new FileInfo(latest);
        return new StoredManifestInfo(latest, fi.LastWriteTimeUtc, -1, fi.Length, "");
    }

    private static string MetaPath(string manifestPath) =>
        Path.ChangeExtension(manifestPath, null) + ".meta.json";

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}

public enum RehomeApplyOutcome
{
    /// <summary>Folder moved from unknown/ to its creator folder.</summary>
    Moved,
    /// <summary>Target already exists — left untouched (never overwritten or merged).</summary>
    SkippedTargetExists,
    /// <summary>Source is gone and target exists: a previous apply already did this move.</summary>
    AlreadyApplied,
    /// <summary>Source is gone and target missing — nothing to do.</summary>
    SourceMissing,
    /// <summary>The plan entry wasn't Movable when the report was written.</summary>
    NotMovable,
    /// <summary>Source not under sources/&lt;slug&gt;/unknown or target not under sources/&lt;slug&gt; (or inside unknown/), or a symlink.</summary>
    RefusedOutsideSource,
    /// <summary>IO error during the move; nothing deleted.</summary>
    Failed,
}

public sealed record RehomeApplyItem(string? Folder, string? TargetPath, string? ExternalId, RehomeApplyOutcome Outcome, string? Message = null);

public sealed record RehomeApplyResult(
    string Report, string LogPath, DateTime AppliedAt,
    IReadOnlyDictionary<string, int> Summary, IReadOnlyList<RehomeApplyItem> Items)
{
    public int Moved => Items.Count(i => i.Outcome == RehomeApplyOutcome.Moved);
}

/// <summary>
/// Applies a reviewed <c>rehome-unknown-*.json</c> report written by the dry run / plan step.
/// Safety rules: moves only inside &lt;sourceDir&gt; (from unknown/ to a creator folder), never
/// overwrites or merges into an existing target, never deletes, writes an apply log, and is
/// idempotent (re-running reports AlreadyApplied and moves nothing).
/// </summary>
public static class RehomeApplier
{
    public static string ReportDir(string sourceDir) => Path.Combine(sourceDir, ".forgekeeper-reports");

    /// <summary>Resolve a bare report filename to a path in the report dir. Throws on anything else.</summary>
    public static string ResolveReport(string sourceDir, string reportName)
    {
        if (string.IsNullOrWhiteSpace(reportName))
            throw new ArgumentException("Report name is required");
        var name = reportName.Trim();
        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name += ".json";
        if (name != Path.GetFileName(name) || name.Contains("..") || name.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException("Report must be a bare file name inside .forgekeeper-reports");
        if (!name.StartsWith("rehome-unknown-", StringComparison.Ordinal) ||
            name.StartsWith("rehome-unknown-applied-", StringComparison.Ordinal))
            throw new ArgumentException("Report must be a rehome-unknown-*.json plan");
        var path = Path.Combine(ReportDir(sourceDir), name);
        if (!File.Exists(path)) throw new FileNotFoundException($"Report '{name}' not found", path);
        return path;
    }

    public static RehomeApplyResult Apply(string sourceDir, string slug, string reportName, DateTime? now = null)
    {
        var reportPath = ResolveReport(sourceDir, reportName);
        var root = WithSep(Path.GetFullPath(sourceDir));
        var unknownRoot = WithSep(Path.Combine(root, "unknown"));
        var at = now ?? DateTime.UtcNow;

        var results = new List<RehomeApplyItem>();
        using (var doc = JsonDocument.Parse(File.ReadAllText(reportPath)))
        {
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Report has no 'items' array");

            foreach (var it in items.EnumerateArray())
            {
                var folder = Str(it, "folder");
                var target = Str(it, "targetPath");
                var id = Str(it, "externalId");
                var status = Str(it, "status");
                results.Add(ApplyOne(root, unknownRoot, folder, target, id, status));
            }
        }

        var summary = Enum.GetValues<RehomeApplyOutcome>()
            .ToDictionary(o => o.ToString(), o => results.Count(r => r.Outcome == o));
        var logDir = ReportDir(sourceDir);
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, $"rehome-apply-{slug}-{at:yyyyMMdd-HHmmssfff}.json");
        for (var i = 1; File.Exists(logPath); i++)
            logPath = Path.Combine(logDir, $"rehome-apply-{slug}-{at:yyyyMMdd-HHmmssfff}-{i}.json");

        var result = new RehomeApplyResult(Path.GetFileName(reportPath), logPath, at, summary, results);
        File.WriteAllText(logPath, JsonSerializer.Serialize(result, UploadedManifestStore.JsonOpts));
        return result;
    }

    private static RehomeApplyItem ApplyOne(string root, string unknownRoot, string? folder, string? target, string? id, string? status)
    {
        if (!string.Equals(status, nameof(RehomeStatus.Movable), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(target))
            return new(folder, target, id, RehomeApplyOutcome.NotMovable, status);

        string src, dst;
        try
        {
            src = Path.GetFullPath(folder);
            dst = Path.GetFullPath(target);
        }
        catch (Exception ex)
        {
            return new(folder, target, id, RehomeApplyOutcome.RefusedOutsideSource, ex.Message);
        }

        var cmp = StringComparison.Ordinal;
        if (!src.StartsWith(unknownRoot, cmp) || src.Length <= unknownRoot.Length)
            return new(folder, target, id, RehomeApplyOutcome.RefusedOutsideSource, "source is not inside unknown/");
        if (!dst.StartsWith(root, cmp) || WithSep(dst).StartsWith(unknownRoot, cmp) ||
            WithSep(dst) == unknownRoot)
            return new(folder, target, id, RehomeApplyOutcome.RefusedOutsideSource, "target is not inside the source dir (outside unknown/)");
        // Target must be <root>/<creator>/<folder>: exactly two levels below the source root.
        if (Path.GetRelativePath(root, dst).Split(Path.DirectorySeparatorChar).Length != 2)
            return new(folder, target, id, RehomeApplyOutcome.RefusedOutsideSource, "target must be <creator>/<folder>");

        var srcExists = Directory.Exists(src);
        var dstExists = Directory.Exists(dst) || File.Exists(dst);
        if (!srcExists)
            return new(folder, target, id, dstExists ? RehomeApplyOutcome.AlreadyApplied : RehomeApplyOutcome.SourceMissing);
        if (new DirectoryInfo(src).LinkTarget != null)
            return new(folder, target, id, RehomeApplyOutcome.RefusedOutsideSource, "source is a symlink");
        var parent = Path.GetDirectoryName(dst)!;
        if (new DirectoryInfo(parent) is { Exists: true, LinkTarget: not null })
            return new(folder, target, id, RehomeApplyOutcome.RefusedOutsideSource, "creator folder is a symlink");
        if (dstExists)
            return new(folder, target, id, RehomeApplyOutcome.SkippedTargetExists);

        try
        {
            Directory.CreateDirectory(parent);
            Directory.Move(src, dst); // fails rather than overwriting if dst appeared meanwhile
            return new(folder, target, id, RehomeApplyOutcome.Moved);
        }
        catch (Exception ex)
        {
            return new(folder, target, id, RehomeApplyOutcome.Failed, ex.Message);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string WithSep(string p) =>
        p.EndsWith(Path.DirectorySeparatorChar) ? p : p + Path.DirectorySeparatorChar;
}
