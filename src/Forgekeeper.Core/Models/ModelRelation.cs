namespace Forgekeeper.Core.Models;

/// <summary>
/// Represents a relationship between two Model3D entities.
/// Supports self-referencing many-to-many relationships like
/// companion models, remixes, alternates, etc.
/// </summary>
public class ModelRelation
{
    public Guid Id { get; set; }

    /// <summary>
    /// The source model in the relationship.
    /// </summary>
    public Guid ModelId { get; set; }

    /// <summary>
    /// The related/target model in the relationship.
    /// </summary>
    public Guid RelatedModelId { get; set; }

    /// <summary>
    /// Type of relationship: companion, collection, remix, alternate, base.
    /// Matches the relatedModels[].relation field in metadata.json.
    /// </summary>
    public string RelationType { get; set; } = "collection";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Model3D Model { get; set; } = null!;
    public Model3D RelatedModel { get; set; } = null!;
}
