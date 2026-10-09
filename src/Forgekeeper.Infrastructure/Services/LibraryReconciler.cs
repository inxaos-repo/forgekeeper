using System.Text;
using System.Text.Json;
using Forgekeeper.PluginSdk;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>How a manifest item relates to what is already on disk.</summary>
public enum ReconcileStatus
{
    /// <summary>Found by source id (DB or metadata.json) — the reliable match.</summary>
    OnDiskById,
    /// <summary>No id match, but the fuzzy creator/name match found a folder.</summary>
    OnDiskByName,
    /// <summary>Nothing on disk — a real sync would create a new folder and download.</summary>
    New,
    /// <summary>Ambiguous: several folders claim this id, or id and name point to different folders.</summary>
    Conflict,
}

public sealed record ReconcileEntry(
    string ExternalId,
    string Name,
    string? CreatorName,
    ReconcileStatus Status,
    string? ExistingPath,
    string TargetPath,
    string? Note,
    string? Sources);

/// <summary>
/// Index of source id → folder(s), built from the DB (Models.SourceId) and from
/// metadata.json files on disk (externalId). Keys are normalized to prefixed MMF ids:
/// a bare number is treated as "object-N" (legacy convention) and is never confused
/// with "bundle-N".
/// </summary>
public sealed class SourceIdIndex
{
    private readonly Dictionary<string, HashSet<string>> _db = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _meta = new(StringComparer.OrdinalIgnoreCase);

    public int DbCount => _db.Count;
    public int MetadataCount => _meta.Count;

    public static string NormalizeKey(string id)
    {
        id = id.Trim();
        return id.All(char.IsDigit) ? $"object-{id}" : id.ToLowerInvariant();
    }

    public void AddFromDb(string? sourceId, string? basePath) => Add(_db, sourceId, basePath);
    public void AddFromMetadata(string? externalId, string? folder) => Add(_meta, externalId, folder);

    private static void Add(Dictionary<string, HashSet<string>> map, string? id, string? path)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(path)) return;
        var key = NormalizeKey(id);
        if (!map.TryGetValue(key, out var set)) map[key] = set = new(StringComparer.Ordinal);
        set.Add(Path.TrimEndingDirectorySeparator(path));
    }

    /// <summary>DB first, then metadata.json. Returns every candidate folder from the first layer that has any.</summary>
    public IReadOnlyCollection<string> Lookup(string externalId)
    {
        var key = NormalizeKey(externalId);
        if (_db.TryGetValue(key, out var d) && d.Count > 0) return d;
        if (_meta.TryGetValue(key, out var m) && m.Count > 0) return m;
        return [];
    }

    /// <summary>Scan &lt;sourceDir&gt;/&lt;creator&gt;/&lt;model&gt;/metadata.json for externalId.</summary>
    public void ScanMetadata(string sourceDir)
    {
        if (!Directory.Exists(sourceDir)) return;
        foreach (var creator in SafeDirs(sourceDir))
        foreach (var model in SafeDirs(creator))
        {
            var id = ReadExternalId(Path.Combine(model, "metadata.json"));
            if (id != null) AddFromMetadata(id, model);
        }
    }

    internal static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).Where(d => !Path.GetFileName(d).StartsWith('.')).ToList(); }
        catch { return []; }
    }

    public static string? ReadExternalId(string metadataPath)
    {
        if (!File.Exists(metadataPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metadataPath));
            if (doc.RootElement.TryGetProperty("externalId", out var e))
                return e.ValueKind == JsonValueKind.Number ? e.GetRawText() : e.GetString();
        }
        catch { /* unreadable metadata — treat as no id */ }
        return null;
    }
}

/// <summary>Pure classification + report writing for DRY_RUN and the unknown/ re-home plan.</summary>
public static class LibraryReconciler
{
    public static ReconcileEntry Classify(
        ScrapedModel model,
        SourceIdIndex index,
        string sourceDir,
        Func<string, string, string?> nameMatch,
        Func<string, string> sanitize)
    {
        var creatorDir = sanitize(model.CreatorName ?? "unknown");
        var modelName = sanitize(model.Name);
        var target = Path.Combine(sourceDir, creatorDir, modelName);
        var byId = index.Lookup(model.ExternalId);
        var byName = nameMatch(creatorDir, modelName);
        var sources = model.Acquisitions.Count > 0
            ? string.Join('|', model.Acquisitions.Select(a => a.Source).Distinct())
            : null;

        if (byId.Count > 1)
            return new(model.ExternalId, model.Name, model.CreatorName, ReconcileStatus.Conflict, byId.First(), target,
                $"{byId.Count} folders claim this id: {string.Join(" ; ", byId)}", sources);

        if (byId.Count == 1)
        {
            var idPath = byId.First();
            if (byName != null && !SamePath(byName, idPath))
            {
                // A name match in a different folder is worth reviewing, unless that folder
                // belongs to a different id (normal for same-named items, e.g. object vs bundle).
                var otherId = SourceIdIndex.ReadExternalId(Path.Combine(byName, "metadata.json"));
                if (otherId == null || SourceIdIndex.NormalizeKey(otherId) == SourceIdIndex.NormalizeKey(model.ExternalId))
                    return new(model.ExternalId, model.Name, model.CreatorName, ReconcileStatus.Conflict, idPath, target,
                        $"id match {idPath} but name match {byName}", sources);
            }
            var note = idPath.Contains($"{Path.DirectorySeparatorChar}unknown{Path.DirectorySeparatorChar}") ? "in unknown/ — re-home candidate" : null;
            return new(model.ExternalId, model.Name, model.CreatorName, ReconcileStatus.OnDiskById, idPath, target, note, sources);
        }

        if (byName != null)
        {
            // Name matched a folder that is tagged with a DIFFERENT id → not ours (object-N vs bundle-N).
            var otherId = SourceIdIndex.ReadExternalId(Path.Combine(byName, "metadata.json"));
            if (otherId != null && SourceIdIndex.NormalizeKey(otherId) != SourceIdIndex.NormalizeKey(model.ExternalId))
                return new(model.ExternalId, model.Name, model.CreatorName, ReconcileStatus.Conflict, byName, target,
                    $"name matches folder owned by {otherId}", sources);
            return new(model.ExternalId, model.Name, model.CreatorName, ReconcileStatus.OnDiskByName, byName, target, null, sources);
        }

        return new(model.ExternalId, model.Name, model.CreatorName, ReconcileStatus.New, null, target, null, sources);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                      Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.Ordinal);

    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Write &lt;dir&gt;/&lt;baseName&gt;.json and .csv. Returns the two paths.</summary>
    public static (string Json, string Csv) WriteReport(string dir, string baseName, IReadOnlyList<ReconcileEntry> entries)
    {
        Directory.CreateDirectory(dir);
        var summary = Enum.GetValues<ReconcileStatus>().ToDictionary(s => s.ToString(), s => entries.Count(e => e.Status == s));
        var jsonPath = Path.Combine(dir, baseName + ".json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(new
        {
            generatedAt = DateTime.UtcNow,
            total = entries.Count,
            summary,
            items = entries,
        }, ReportJson));

        var csvPath = Path.Combine(dir, baseName + ".csv");
        var sb = new StringBuilder("externalId,name,creator,status,existingPath,targetPath,sources,note\n");
        foreach (var e in entries)
            sb.AppendLine(string.Join(',', new[] { e.ExternalId, e.Name, e.CreatorName, e.Status.ToString(), e.ExistingPath, e.TargetPath, e.Sources, e.Note }.Select(Csv)));
        File.WriteAllText(csvPath, sb.ToString());
        return (jsonPath, csvPath);
    }

    internal static string Csv(string? v)
    {
        v ??= "";
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
    }
}

public enum RehomeStatus { Movable, TargetExists, NoMetadata, NotInManifest, NoCreator }

public sealed record RehomeEntry(string Folder, string? ExternalId, string? CreatorName, string? TargetPath, RehomeStatus Status);

/// <summary>
/// Maps every &lt;sourceDir&gt;/unknown/* folder to its proper creator folder using the
/// folder's metadata.json externalId + the manifest. Planning is read-only.
/// <see cref="Apply"/> moves folders and must only be called behind an explicit opt-in flag.
/// </summary>
public static class UnknownRehomePlanner
{
    public static IReadOnlyList<RehomeEntry> Plan(string sourceDir, IEnumerable<ScrapedModel> manifest, Func<string, string> sanitize)
    {
        var byId = new Dictionary<string, ScrapedModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in manifest) byId.TryAdd(SourceIdIndex.NormalizeKey(m.ExternalId), m);

        var unknown = Path.Combine(sourceDir, "unknown");
        var result = new List<RehomeEntry>();
        foreach (var folder in SourceIdIndex.SafeDirs(unknown).OrderBy(f => f, StringComparer.Ordinal))
        {
            var id = SourceIdIndex.ReadExternalId(Path.Combine(folder, "metadata.json"));
            if (id == null) { result.Add(new(folder, null, null, null, RehomeStatus.NoMetadata)); continue; }
            if (!byId.TryGetValue(SourceIdIndex.NormalizeKey(id), out var m)) { result.Add(new(folder, id, null, null, RehomeStatus.NotInManifest)); continue; }
            if (string.IsNullOrWhiteSpace(m.CreatorName)) { result.Add(new(folder, id, null, null, RehomeStatus.NoCreator)); continue; }

            var target = Path.Combine(sourceDir, sanitize(m.CreatorName), Path.GetFileName(folder));
            result.Add(new(folder, id, m.CreatorName, target, Directory.Exists(target) ? RehomeStatus.TargetExists : RehomeStatus.Movable));
        }
        return result;
    }

    public static (string Json, string Csv) WriteReport(string dir, string baseName, IReadOnlyList<RehomeEntry> plan)
    {
        Directory.CreateDirectory(dir);
        var jsonPath = Path.Combine(dir, baseName + ".json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(new
        {
            generatedAt = DateTime.UtcNow,
            total = plan.Count,
            summary = Enum.GetValues<RehomeStatus>().ToDictionary(s => s.ToString(), s => plan.Count(e => e.Status == s)),
            items = plan,
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        var csvPath = Path.Combine(dir, baseName + ".csv");
        var sb = new StringBuilder("folder,externalId,creator,targetPath,status\n");
        foreach (var e in plan)
            sb.AppendLine(string.Join(',', new[] { e.Folder, e.ExternalId, e.CreatorName, e.TargetPath, e.Status.ToString() }.Select(LibraryReconciler.Csv)));
        File.WriteAllText(csvPath, sb.ToString());
        return (jsonPath, csvPath);
    }

    /// <summary>DESTRUCTIVE: moves Movable folders. Only call when REHOME_UNKNOWN_APPLY=true. Returns moved count.</summary>
    public static int Apply(IReadOnlyList<RehomeEntry> plan)
    {
        int moved = 0;
        foreach (var e in plan.Where(p => p.Status == RehomeStatus.Movable && p.TargetPath != null))
        {
            if (Directory.Exists(e.TargetPath)) continue; // re-check: never overwrite
            Directory.CreateDirectory(Path.GetDirectoryName(e.TargetPath!)!);
            Directory.Move(e.Folder, e.TargetPath!);
            moved++;
        }
        return moved;
    }
}
