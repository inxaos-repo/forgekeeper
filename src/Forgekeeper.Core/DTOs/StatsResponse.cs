using System.Text.Json.Serialization;

namespace Forgekeeper.Core.DTOs;

public class StatsResponse
{
    public int TotalModels { get; set; }
    public int TotalCreators { get; set; }
    public int TotalFiles { get; set; }
    public long TotalSizeBytes { get; set; }
    public int PrintedCount { get; set; }
    public int UnprintedCount { get; set; }
    public Dictionary<string, int> ModelsBySource { get; set; } = new();
    public Dictionary<string, int> ModelsByCategory { get; set; } = new();
    public Dictionary<string, int> FilesByType { get; set; } = new();
    public List<CreatorStatsItem> TopCreators { get; set; } = [];

    // Frontend-friendly array versions of the dictionaries
    public List<SourceStatsItem> BySource => ModelsBySource
        .Select(kvp => new SourceStatsItem { Source = kvp.Key, Count = kvp.Value })
        .OrderByDescending(x => x.Count).ToList();
    public List<CategoryStatsItem> ByCategory => ModelsByCategory
        .Select(kvp => new CategoryStatsItem { Category = kvp.Key, Count = kvp.Value })
        .OrderByDescending(x => x.Count).ToList();
    public List<FileTypeStatsItem> ByFileType => FilesByType
        .Select(kvp => new FileTypeStatsItem { FileType = kvp.Key, Count = kvp.Value })
        .OrderByDescending(x => x.Count).ToList();
}

public class SourceStatsItem
{
    public string Source { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class CategoryStatsItem
{
    public string Category { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class FileTypeStatsItem
{
    public string FileType { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class CreatorStatsItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ModelCount { get; set; }
    public long TotalSizeBytes { get; set; }
}
