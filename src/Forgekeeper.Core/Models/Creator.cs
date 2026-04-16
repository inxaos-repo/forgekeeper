using Forgekeeper.Core.Enums;

namespace Forgekeeper.Core.Models;

public class Creator
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public SourceType Source { get; set; }
    public string? SourceUrl { get; set; }
    public string? ExternalId { get; set; }
    public string? AvatarUrl { get; set; }
    public int ModelCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public List<Model3D> Models { get; set; } = [];
}
