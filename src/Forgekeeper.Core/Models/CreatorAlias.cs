namespace Forgekeeper.Core.Models;

/// <summary>Multi-source attribution phase 2 (#92): alternative names a creator is known by.</summary>
public class CreatorAlias
{
    public Guid Id { get; set; }
    public Guid CreatorId { get; set; }
    public string Alias { get; set; } = string.Empty;
    public string NormalizedAlias { get; set; } = string.Empty;

    /// <summary>"name" (spelling variant), "handle", "merged" (name of a merged creator).</summary>
    public string Kind { get; set; } = "name";
    public Guid? SourceId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Creator? Creator { get; set; }
}
