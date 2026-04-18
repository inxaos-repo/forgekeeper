namespace Forgekeeper.Core.Models;

public class SyncRun
{
    public Guid Id { get; set; }
    public string PluginSlug { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string Status { get; set; } = "running"; // running, completed, failed, cancelled
    public int TotalModels { get; set; }
    public int ScrapedModels { get; set; }
    public int FailedModels { get; set; }
    public int SkippedModels { get; set; }
    public int FilesDownloaded { get; set; }
    public long BytesDownloaded { get; set; }
    public string? Error { get; set; }
    public double? DurationSeconds { get; set; }
    public int LastProcessedIndex { get; set; }
}
