using System.Text.RegularExpressions;
using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Interfaces;

namespace Forgekeeper.Infrastructure.SourceAdapters;

/// <summary>
/// Handles Patreon creator drops: sources/patreon/CreatorName/YYYY-MM Release Name/ModelName/
/// </summary>
public partial class PatreonSourceAdapter : ISourceAdapter
{
    public SourceType SourceType => SourceType.Patreon;
    public string SourceSlug => "patreon";

    public bool CanHandle(string directoryPath)
    {
        var normalized = directoryPath.Replace('\\', '/');
        return normalized.Contains("/sources/patreon/", StringComparison.OrdinalIgnoreCase);
    }

    public ParsedModelInfo? ParseModelDirectory(string modelDirectoryPath)
    {
        // Patreon can be:
        //   .../CreatorName/YYYY-MM Release Name/ModelName/  (3 levels deep)
        //   .../CreatorName/ModelName/  (2 levels deep, simpler case)
        var dirInfo = new DirectoryInfo(modelDirectoryPath);
        var parent = dirInfo.Parent;
        if (parent == null) return null;

        var grandParent = parent.Parent;

        // Check if parent looks like a date-based release (YYYY-MM pattern)
        if (grandParent != null && DateReleasePattern().IsMatch(parent.Name))
        {
            // 3-level: grandParent=Creator, parent=Release, dir=Model
            return new ParsedModelInfo
            {
                CreatorName = grandParent.Name,
                ModelName = $"{parent.Name} - {dirInfo.Name}",
                Source = SourceType.Patreon,
                SourceSlug = "patreon",
                BasePath = modelDirectoryPath
            };
        }

        // 2-level: parent=Creator, dir=Model
        return new ParsedModelInfo
        {
            CreatorName = parent.Name,
            ModelName = dirInfo.Name,
            Source = SourceType.Patreon,
            SourceSlug = "patreon",
            BasePath = modelDirectoryPath
        };
    }

    [GeneratedRegex(@"^\d{4}-\d{2}")]
    private static partial Regex DateReleasePattern();
}
