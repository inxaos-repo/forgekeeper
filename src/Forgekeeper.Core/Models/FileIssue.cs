namespace Forgekeeper.Core.Models;

/// <summary>
/// Tracks recurring file-level errors (thumbnail failures, missing files, hash errors, etc.)
/// for surfacing in the UI and Prometheus metrics. Upserted on (FilePath, IssueType).
/// </summary>
public class FileIssue
{
    public Guid Id { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public Guid? VariantId { get; set; }
    public Guid? ModelId { get; set; }

    /// <summary>
    /// Issue category: "thumbnail_fail", "hash_fail", "corrupt", "missing"
    /// </summary>
    public string IssueType { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }

    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public int Attempts { get; set; } = 1;
    public bool Dismissed { get; set; } = false;
    public string? DismissedBy { get; set; }
    public DateTime? DismissedAt { get; set; }

    // Navigation
    public Variant? Variant { get; set; }
    public Model3D? Model { get; set; }
}
