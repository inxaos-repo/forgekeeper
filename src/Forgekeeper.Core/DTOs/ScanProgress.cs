namespace Forgekeeper.Core.DTOs;

public class ScanProgress
{
    public bool IsRunning { get; set; }
    public string Status { get; set; } = "idle";
    public int DirectoriesScanned { get; set; }
    public int ModelsFound { get; set; }
    public int FilesFound { get; set; }
    public int NewModels { get; set; }
    public int UpdatedModels { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public double? ElapsedSeconds { get; set; }
    public string? Error { get; set; }
}
