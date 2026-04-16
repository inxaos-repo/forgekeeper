namespace Forgekeeper.Core.Models;

public class Tag
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    // Navigation
    public List<Model3D> Models { get; set; } = [];
}
