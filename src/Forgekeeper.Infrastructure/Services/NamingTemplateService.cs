using System.Text.RegularExpressions;
using Forgekeeper.Core.DTOs;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// Applies naming templates to 3D model paths using token replacement.
///
/// Supported tokens:
///   {Creator}          - Creator display name (as-is)
///   {Creator CleanName} - Creator name with invalid path chars stripped
///   {Model}            - Model name (as-is)
///   {Model CleanName}  - Model name with invalid path chars stripped
///   {Variant}          - Variant label (e.g., "supported", "presupported")
///   {Scale}            - Scale (e.g., "28mm")
///   {Source}           - Source slug (e.g., "mmf", "patreon")
///   {Category}         - Category
///   {GameSystem}       - Game system
///   {FileType}         - File extension without dot (e.g., "STL")
///   {DateAdded}        - Date added formatted as yyyy-MM-dd
///   {Collection}       - Collection name
/// </summary>
public class NamingTemplateService
{
    private static readonly char[] InvalidPathChars =
        Path.GetInvalidFileNameChars()
            .Concat(Path.GetInvalidPathChars())
            .Distinct()
            .ToArray();

    // Matches tokens like {Creator}, {Creator CleanName}, {Model CleanName}, etc.
    private static readonly Regex TokenRegex = new(
        @"\{(?<token>[^}]+)\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Replace all tokens in a template string with values from the provided dictionary.
    /// Unknown tokens are left as-is.
    /// </summary>
    public string ApplyTemplate(string template, Dictionary<string, string> tokens)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(tokens);

        return TokenRegex.Replace(template, match =>
        {
            var tokenKey = match.Groups["token"].Value;

            // Exact match first
            if (tokens.TryGetValue(tokenKey, out var value))
                return value;

            // Case-insensitive fallback
            var caseInsensitive = tokens
                .FirstOrDefault(kvp => kvp.Key.Equals(tokenKey, StringComparison.OrdinalIgnoreCase));
            if (caseInsensitive.Key != null)
                return caseInsensitive.Value;

            // Unresolved — return original token unchanged
            return match.Value;
        });
    }

    /// <summary>
    /// Build a token dictionary from a ModelRenameInput.
    /// </summary>
    public Dictionary<string, string> BuildTokens(ModelRenameInput model)
    {
        var dateAdded = model.DateAdded.HasValue
            ? model.DateAdded.Value.ToString("yyyy-MM-dd")
            : DateTime.UtcNow.ToString("yyyy-MM-dd");

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Creator"] = model.CreatorName,
            ["Creator CleanName"] = CleanName(model.CreatorName),
            ["Model"] = model.ModelName,
            ["Model CleanName"] = CleanName(model.ModelName),
            ["Variant"] = model.Variant ?? "",
            ["Scale"] = model.Scale ?? "",
            ["Source"] = model.Source ?? "",
            ["Category"] = model.Category ?? "",
            ["GameSystem"] = model.GameSystem ?? "",
            ["FileType"] = model.FileType?.ToUpperInvariant() ?? "",
            ["DateAdded"] = dateAdded,
            ["Collection"] = model.Collection ?? "",
        };
    }

    /// <summary>
    /// Preview what paths a set of models would be renamed to under a given template.
    /// Does not move any files — pure preview.
    /// </summary>
    public List<RenamePreview> PreviewRename(List<ModelRenameInput> models, string template)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(template);

        var previews = new List<RenamePreview>(models.Count);

        foreach (var model in models)
        {
            var tokens = BuildTokens(model);
            var newBasePath = ApplyTemplate(template, tokens);

            // Normalise: collapse double separators, trim trailing sep
            newBasePath = NormalisePath(newBasePath);

            var fileRenames = model.Files.Select(file =>
            {
                var fileName = Path.GetFileName(file);
                var relativeDirFromBase = Path.GetDirectoryName(
                    Path.GetRelativePath(model.CurrentPath, file)) ?? "";

                var newFilePath = string.IsNullOrEmpty(relativeDirFromBase)
                    ? Path.Combine(newBasePath, fileName)
                    : Path.Combine(newBasePath, relativeDirFromBase, fileName);

                return new FileRenamePreview
                {
                    From = file,
                    To = NormalisePath(newFilePath),
                };
            }).ToList();

            previews.Add(new RenamePreview
            {
                ModelId = model.ModelId,
                CurrentPath = model.CurrentPath,
                NewPath = newBasePath,
                Files = fileRenames,
            });
        }

        return previews;
    }

    // -------- internals --------

    /// <summary>Strip characters invalid in file/path names and replace with underscores.</summary>
    public static string CleanName(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var cleaned = string.Concat(input.Select(c =>
            InvalidPathChars.Contains(c) || c == ':' ? '_' : c));

        // Collapse repeated underscores / spaces
        cleaned = Regex.Replace(cleaned, @"_+", "_");
        cleaned = cleaned.Trim().TrimStart('.').TrimEnd('.');

        return cleaned;
    }

    private static string NormalisePath(string path)
    {
        // Normalise separators
        path = path.Replace('/', Path.DirectorySeparatorChar)
                   .Replace('\\', Path.DirectorySeparatorChar);

        // Collapse doubled separators (but preserve leading // on Unix)
        var sep = Path.DirectorySeparatorChar.ToString();
        while (path.Contains(sep + sep, StringComparison.Ordinal))
            path = path.Replace(sep + sep, sep);

        return path.TrimEnd(Path.DirectorySeparatorChar);
    }
}
