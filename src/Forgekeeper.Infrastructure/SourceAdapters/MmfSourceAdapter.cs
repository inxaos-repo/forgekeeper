using System.Text.RegularExpressions;
using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Interfaces;

namespace Forgekeeper.Infrastructure.SourceAdapters;

public partial class MmfSourceAdapter : ISourceAdapter
{
    public SourceType SourceType => SourceType.Mmf;
    public string SourceSlug => "mmf";

    public bool CanHandle(string directoryPath)
    {
        // MMF directories are under sources/mmf/ or contain MMFDownloader marker
        var normalized = directoryPath.Replace('\\', '/');
        return normalized.Contains("/sources/mmf/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/MMFDownloader/", StringComparison.OrdinalIgnoreCase);
    }

    public ParsedModelInfo? ParseModelDirectory(string modelDirectoryPath)
    {
        // Expected: .../CreatorName/ModelName - ModelID/
        var dirInfo = new DirectoryInfo(modelDirectoryPath);
        var parent = dirInfo.Parent;
        if (parent == null) return null;

        var creatorName = parent.Name;
        var modelDirName = dirInfo.Name;

        // Parse "ModelName - 12345" pattern
        string modelName;
        string? sourceId = null;

        var match = ModelIdPattern().Match(modelDirName);
        if (match.Success)
        {
            modelName = match.Groups[1].Value.Trim();
            sourceId = match.Groups[2].Value;
        }
        else
        {
            modelName = modelDirName;
        }

        return new ParsedModelInfo
        {
            CreatorName = creatorName,
            ModelName = modelName,
            SourceId = sourceId,
            Source = SourceType.Mmf,
            SourceSlug = "mmf",
            BasePath = modelDirectoryPath
        };
    }

    [GeneratedRegex(@"^(.+?)\s*-\s*(\d+)$")]
    private static partial Regex ModelIdPattern();
}
