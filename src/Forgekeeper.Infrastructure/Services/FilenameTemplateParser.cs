using System.Text;
using System.Text.RegularExpressions;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// Mp3tag-style "Guess from Filename" parser.
/// Converts a template like <c>{id} - {creator} - {name}</c> into a regex
/// and extracts named metadata fields from a directory/filename string.
/// </summary>
public class FilenameTemplateParser
{
    // All known variable names (case-insensitive in the template)
    private static readonly HashSet<string> KnownVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "creator", "id", "category", "gameSystem", "scale", "source", "ignore"
    };

    /// <summary>
    /// Parse a filename/directory name using a template pattern.
    /// </summary>
    /// <param name="template">
    /// Template with <c>{variable}</c> placeholders.  
    /// Variables: {name}, {creator}, {id}, {category}, {gameSystem}, {scale}, {source}, {ignore}
    /// </param>
    /// <param name="input">The filename or directory name to parse.</param>
    /// <returns>
    /// A dictionary of extracted field values (not including {ignore}), or <c>null</c> if
    /// the input does not match the template.
    /// </returns>
    public Dictionary<string, string>? Parse(string template, string input)
    {
        if (string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(input))
            return null;

        var pattern = BuildRegex(template);
        if (pattern == null) return null;

        var match = pattern.Match(input.Trim());
        if (!match.Success) return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Group group in match.Groups)
        {
            // Named groups only (skip the overall match group "0")
            if (!int.TryParse(group.Name, out _) && group.Success)
            {
                // Skip {ignore} — matched but not extracted
                if (!group.Name.Equals("ignore", StringComparison.OrdinalIgnoreCase))
                    result[group.Name] = group.Value.Trim();
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Apply the template to a batch of inputs and return parse results for each.
    /// </summary>
    public List<ParseResult> ParseBatch(string template, IEnumerable<string> inputs)
    {
        var results = new List<ParseResult>();
        var pattern = BuildRegex(template);

        foreach (var input in inputs)
        {
            var parsed = pattern != null ? ParseWithRegex(pattern, input) : null;
            results.Add(new ParseResult
            {
                Input = input,
                Parsed = parsed,
                Success = parsed != null,
            });
        }

        return results;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Internals
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Convert a template string to a compiled <see cref="Regex"/>, or null if the
    /// template contains no variables.
    /// </summary>
    private static Regex? BuildRegex(string template)
    {
        // Tokenise: split into alternating [literal, {variable}, literal, {variable}, ...]
        var tokens = Tokenise(template);
        if (!tokens.Any(t => t.IsVariable)) return null;

        // Find index of the last variable token so we can make it greedy
        int lastVarIndex = -1;
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            if (tokens[i].IsVariable) { lastVarIndex = i; break; }
        }

        // Track variable names already seen so duplicates get numeric suffixes
        // (e.g. two {ignore} uses: ignore, ignore_2)
        var varCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder("^");

        for (int i = 0; i < tokens.Count; i++)
        {
            var tok = tokens[i];

            if (tok.IsVariable)
            {
                var varName = tok.Value; // e.g. "creator", "ignore"
                varCount.TryGetValue(varName, out var count);
                var groupName = count == 0 ? varName : $"{varName}_{count + 1}";
                varCount[varName] = count + 1;

                bool isLast = (i == lastVarIndex);
                // Last variable is greedy to consume the rest of the string
                sb.Append($"(?<{groupName}>{(isLast ? ".+" : ".+?")})");
            }
            else
            {
                // Literal segment — apply whitespace and separator flexibility
                sb.Append(LiteralToPattern(tok.Value));
            }
        }

        sb.Append('$');

        try
        {
            return new Regex(sb.ToString(),
                RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline,
                TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string>? ParseWithRegex(Regex pattern, string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var match = pattern.Match(input.Trim());
        if (!match.Success) return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Group group in match.Groups)
        {
            if (!int.TryParse(group.Name, out _) && group.Success)
            {
                if (!group.Name.StartsWith("ignore", StringComparison.OrdinalIgnoreCase))
                    result[group.Name] = group.Value.Trim();
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Split a template into a list of literal and variable tokens.
    /// E.g. "{id} - {creator} - {name}" →
    ///   [Variable("id"), Literal(" - "), Variable("creator"), Literal(" - "), Variable("name")]
    /// </summary>
    private static List<Token> Tokenise(string template)
    {
        var tokens = new List<Token>();
        int pos = 0;

        while (pos < template.Length)
        {
            int open = template.IndexOf('{', pos);
            if (open == -1)
            {
                // Rest is literal
                var tail = template[pos..];
                if (tail.Length > 0) tokens.Add(Token.Literal(tail));
                break;
            }

            // Literal before the '{'
            if (open > pos) tokens.Add(Token.Literal(template[pos..open]));

            int close = template.IndexOf('}', open + 1);
            if (close == -1)
            {
                // Malformed — treat remainder as literal
                tokens.Add(Token.Literal(template[open..]));
                break;
            }

            var varName = template[(open + 1)..close].Trim();
            if (KnownVariables.Contains(varName))
                tokens.Add(Token.Variable(varName));
            else
                tokens.Add(Token.Literal(template[open..(close + 1)])); // unknown → literal

            pos = close + 1;
        }

        return tokens;
    }

    /// <summary>
    /// Convert a literal template segment to a regex fragment with flexible whitespace rules:
    /// <list type="bullet">
    ///   <item><c> - </c> → <c>\s*-\s*</c></item>
    ///   <item><c> </c> (one or more spaces) → <c>\s+</c></item>
    ///   <item>other → <see cref="Regex.Escape"/></item>
    /// </list>
    /// </summary>
    private static string LiteralToPattern(string literal)
    {
        if (string.IsNullOrEmpty(literal)) return string.Empty;

        var sb = new StringBuilder();
        int pos = 0;

        while (pos < literal.Length)
        {
            // Look for " - " style separators (optional whitespace around a hyphen)
            if (literal[pos] == ' ' || literal[pos] == '-')
            {
                // Collect a run of spaces and hyphens
                int start = pos;
                while (pos < literal.Length && (literal[pos] == ' ' || literal[pos] == '-'))
                    pos++;

                var seg = literal[start..pos];
                bool hasHyphen = seg.Contains('-');

                if (hasHyphen)
                    sb.Append(@"\s*-\s*");
                else
                    sb.Append(@"\s+");

                continue;
            }

            // Slash in templates (e.g. "{creator}/{name}") → literal slash
            if (literal[pos] == '/')
            {
                sb.Append(Regex.Escape("/"));
                pos++;
                continue;
            }

            // All other characters — find the next space/hyphen boundary and escape
            int segStart = pos;
            while (pos < literal.Length && literal[pos] != ' ' && literal[pos] != '-' && literal[pos] != '/')
                pos++;

            sb.Append(Regex.Escape(literal[segStart..pos]));
        }

        return sb.ToString();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Token helper record
    // ──────────────────────────────────────────────────────────────────────────

    private sealed record Token(string Value, bool IsVariable)
    {
        public static Token Literal(string value) => new(value, false);
        public static Token Variable(string value) => new(value, true);
    }
}

/// <summary>Result of parsing a single input against a template.</summary>
public class ParseResult
{
    public string Input { get; set; } = "";
    public Dictionary<string, string>? Parsed { get; set; }
    public bool Success { get; set; }
}

/// <summary>
/// Default trash patterns to strip from filenames before parsing.
/// Users can add custom patterns via the trashPatterns parameter.
/// </summary>
public static class FilenameTrashFilter
{
    /// <summary>Built-in patterns to strip from directory/file names before parsing.</summary>
    public static readonly string[] DefaultTrashPatterns =
    [
        // OS artifacts
        "_MACOSX", "__MACOSX", ".DS_Store", "Thumbs.db", "desktop.ini",
        // Duplicate suffixes
        " - Copy", " (1)", " (2)", " (3)", " (4)", " (5)",
        "Copy of ", "Copy_of_",
        // Common noise in 3D printing filenames
        " UPDATED", " UPDATE", " FIXED", " FIX", " NEW",
        " v1.0", " v2.0", " v1", " v2",
        " (FREE)", "(FREE)", "[FREE]",
        " (Pre-Supported)", " (Presupported)",
        " - STL", " STL Files", " STL",
        " - OBJ", " OBJ Files",
        // Patreon-specific
        " - Patreon", " (Patreon Only)", " (Early Access)",
        " - Welcome Pack", " - Sample",
    ];

    /// <summary>
    /// Strip trash patterns from a filename/directory name.
    /// Returns the cleaned string.
    /// </summary>
    public static string Clean(string input, IEnumerable<string>? customPatterns = null)
    {
        var result = input;
        
        foreach (var pattern in DefaultTrashPatterns)
        {
            result = result.Replace(pattern, "", StringComparison.OrdinalIgnoreCase);
        }
        
        if (customPatterns != null)
        {
            foreach (var pattern in customPatterns)
            {
                if (!string.IsNullOrWhiteSpace(pattern))
                    result = result.Replace(pattern, "", StringComparison.OrdinalIgnoreCase);
            }
        }
        
        // Clean up double spaces and trim
        while (result.Contains("  "))
            result = result.Replace("  ", " ");
        
        return result.Trim().TrimEnd('-', '_', '.').Trim();
    }
}
