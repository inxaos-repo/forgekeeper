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
    /// <summary>#110: stub/partial-download folders skipped (no content files, no existing row).</summary>
    public int SkippedStub { get; set; }
    /// <summary>#110: folders no source adapter claimed.</summary>
    public int SkippedNoAdapter { get; set; }
    /// <summary>#110: candidate folders split into nested models (pack layouts).</summary>
    public int NestedContainers { get; set; }
    /// <summary>#110: existing rows for those container folders, now superseded by nested models (left in place).</summary>
    public int SupersededContainerRows { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public double? ElapsedSeconds { get; set; }
    public string? Error { get; set; }
}
