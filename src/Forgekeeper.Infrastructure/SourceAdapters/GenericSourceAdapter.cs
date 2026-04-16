using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Interfaces;

namespace Forgekeeper.Infrastructure.SourceAdapters;

/// <summary>
/// Generic adapter for sources with Creator/Model directory structure.
/// Used for thangs, cults3d, thingiverse, and manual sources.
/// </summary>
public class GenericSourceAdapter : ISourceAdapter
{
    private readonly SourceType _sourceType;
    private readonly string _sourceSlug;

    public GenericSourceAdapter(SourceType sourceType, string sourceSlug)
    {
        _sourceType = sourceType;
        _sourceSlug = sourceSlug;
    }

    public SourceType SourceType => _sourceType;
    public string SourceSlug => _sourceSlug;

    public bool CanHandle(string directoryPath)
    {
        var normalized = directoryPath.Replace('\\', '/');
        return normalized.Contains($"/sources/{_sourceSlug}/", StringComparison.OrdinalIgnoreCase);
    }

    public ParsedModelInfo? ParseModelDirectory(string modelDirectoryPath)
    {
        // Expected: .../CreatorName/ModelName/
        var dirInfo = new DirectoryInfo(modelDirectoryPath);
        var parent = dirInfo.Parent;
        if (parent == null) return null;

        return new ParsedModelInfo
        {
            CreatorName = parent.Name,
            ModelName = dirInfo.Name,
            Source = _sourceType,
            SourceSlug = _sourceSlug,
            BasePath = modelDirectoryPath
        };
    }
}
