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
}

public class CreatorStatsItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ModelCount { get; set; }
    public long TotalSizeBytes { get; set; }
}
