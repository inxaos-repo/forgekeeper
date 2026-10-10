using System.Text.Json;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>One item that failed in a sync run (persisted across runs, #53).</summary>
public sealed record SyncFailedEntry(string Id, string? Creator, string? Title, string Reason,
    DateTime FirstFailedUtc, DateTime LastFailedUtc, int Count);

/// <summary>
/// #53: persistent per-plugin list of failed items (<c>.forgekeeper-reports/failed-{slug}.json</c>).
/// NEW_ONLY runs skip ids in this list (recorded as a skip with the reason) so one bad item cannot
/// block every future run. RETRY_FAILED=true retries them; a later success removes the entry.
/// </summary>
public sealed class SyncFailedLedger
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private readonly Dictionary<string, SyncFailedEntry> _items;
    public string Path { get; }

    private SyncFailedLedger(string path, Dictionary<string, SyncFailedEntry> items) { Path = path; _items = items; }

    public static string PathFor(string reportDir, string slug) => System.IO.Path.Combine(reportDir, $"failed-{slug}.json");

    public static SyncFailedLedger Load(string reportDir, string slug)
    {
        var path = PathFor(reportDir, slug);
        var items = new Dictionary<string, SyncFailedEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path))
                foreach (var e in JsonSerializer.Deserialize<List<SyncFailedEntry>>(File.ReadAllText(path)) ?? [])
                    if (!string.IsNullOrEmpty(e.Id)) items[e.Id] = e;
        }
        catch (JsonException) { /* corrupt ledger: start empty rather than block the sync */ }
        return new SyncFailedLedger(path, items);
    }

    public int Count => _items.Count;
    public bool TryGet(string? id, out SyncFailedEntry? entry)
    {
        entry = null;
        return !string.IsNullOrEmpty(id) && _items.TryGetValue(id, out entry);
    }

    public void RecordFailure(string? id, string? creator, string? title, string reason, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(id)) return;
        _items[id] = _items.TryGetValue(id, out var e)
            ? e with { Reason = reason, LastFailedUtc = nowUtc, Count = e.Count + 1, Creator = creator ?? e.Creator, Title = title ?? e.Title }
            : new SyncFailedEntry(id, creator, title, reason, nowUtc, nowUtc, 1);
    }

    public bool RecordSuccess(string? id) => !string.IsNullOrEmpty(id) && _items.Remove(id);

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_items.Values.OrderBy(e => e.Id).ToList(), Opts));
        File.Move(tmp, Path, overwrite: true);
    }
}
