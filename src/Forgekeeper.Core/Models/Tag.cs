namespace Forgekeeper.Core.Models;

public class Tag
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Distinguishes scraper-provided tags from user-added tags.
    /// Values: "scraper", "user", or null (unknown/legacy).
    /// </summary>
    public string? Source { get; set; }

    // Navigation
    public List<Model3D> Models { get; set; } = [];
}
