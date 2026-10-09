using System.Text;
using System.Text.Json;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>One manifest item skipped during a sync run (written to the per-run skip list).</summary>
public sealed record SyncSkipRecord(int Index, string? Id, string? Creator, string? Title, string Reason)
{
    /// <summary>Writes {name}.json and {name}.csv into <paramref name="dir"/>; returns the JSON path.</summary>
    public static string WriteReport(string dir, string name, IReadOnlyList<SyncSkipRecord> records)
    {
        Directory.CreateDirectory(dir);
        var json = Path.Combine(dir, name + ".json");
        File.WriteAllText(json, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        var sb = new StringBuilder("index,id,creator,title,reason\n");
        foreach (var r in records)
            sb.Append(r.Index).Append(',').Append(Csv(r.Id)).Append(',').Append(Csv(r.Creator)).Append(',')
              .Append(Csv(r.Title)).Append(',').Append(Csv(r.Reason)).Append('\n');
        File.WriteAllText(Path.Combine(dir, name + ".csv"), sb.ToString());
        return json;
    }

    internal static string Csv(string? v)
    {
        v ??= "";
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
