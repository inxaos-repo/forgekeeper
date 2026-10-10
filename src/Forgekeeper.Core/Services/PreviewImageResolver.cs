namespace Forgekeeper.Core.Services;

/// <summary>
/// Resolves a model's PreviewImages entry to either a remote URL (redirect) or a local
/// file path confined to the model's BasePath (#64).
/// </summary>
public static class PreviewImageResolver
{
    public sealed record Resolved(string? RemoteUrl, string? LocalPath);

    public static Resolved? Resolve(string? basePath, string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return null;

        if (Uri.TryCreate(entry, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return new Resolved(uri.ToString(), null);

        if (string.IsNullOrWhiteSpace(basePath)) return null;

        var root = Path.GetFullPath(basePath);
        var candidate = Path.GetFullPath(Path.IsPathRooted(entry) ? entry : Path.Combine(root, entry));
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSep, StringComparison.Ordinal)) return null; // traversal guard
        return new Resolved(null, candidate);
    }

    public static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".webp" => "image/webp",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream",
    };
}
