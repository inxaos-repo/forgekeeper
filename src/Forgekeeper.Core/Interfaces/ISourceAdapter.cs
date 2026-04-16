using Forgekeeper.Core.Enums;

namespace Forgekeeper.Core.Interfaces;

/// <summary>
/// Adapts a source directory structure into Forgekeeper's canonical model.
/// Each source (mmf, thangs, patreon, etc.) implements this interface.
/// Source-agnostic: no platform-specific types or logic in the interface.
/// </summary>
public interface ISourceAdapter
{
    SourceType SourceType { get; }

    /// <summary>
    /// The source slug (e.g., "mmf", "thangs", "patreon") matching the directory name under sources/
    /// </summary>
    string SourceSlug { get; }
    
    /// <summary>
    /// Returns true if this adapter handles the given directory path.
    /// </summary>
    bool CanHandle(string directoryPath);

    /// <summary>
    /// Parses a model directory and returns structured info.
    /// </summary>
    ParsedModelInfo? ParseModelDirectory(string modelDirectoryPath);
}

public class ParsedModelInfo
{
    public string CreatorName { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string? SourceId { get; set; }
    public SourceType Source { get; set; }
    public string SourceSlug { get; set; } = string.Empty;
    public string BasePath { get; set; } = string.Empty;
}
