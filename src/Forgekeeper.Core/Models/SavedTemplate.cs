namespace Forgekeeper.Core.Models;

/// <summary>
/// Named template for filename parsing or directory reorganization.
/// Can be scoped to a specific creator or source for recurring imports.
/// </summary>
public class SavedTemplate
{
    public Guid Id { get; set; }
    
    /// <summary>User-friendly name, e.g., "Artisan Guild Monthly Drop"</summary>
    public string Name { get; set; } = "";
    
    /// <summary>The template string, e.g., "{creator} - {name}"</summary>
    public string Template { get; set; } = "";
    
    /// <summary>What this template is for: "parse" (filename → metadata) or "reorganize" (metadata → filename)</summary>
    public string Type { get; set; } = "parse"; // "parse" or "reorganize"
    
    /// <summary>Optional: scope to a specific creator name (for recurring creator drops)</summary>
    public string? CreatorName { get; set; }
    
    /// <summary>Optional: scope to a specific source slug (e.g., "patreon")</summary>
    public string? SourceSlug { get; set; }
    
    /// <summary>Optional: description of when/how to use this template</summary>
    public string? Description { get; set; }
    
    /// <summary>How many times this template has been applied</summary>
    public int UseCount { get; set; }
    
    /// <summary>Last time this template was used</summary>
    public DateTime? LastUsedAt { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
