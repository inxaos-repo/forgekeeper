using Microsoft.AspNetCore.Mvc;

namespace Forgekeeper.Api.Endpoints;

public static class FileEndpoints
{
    public static void MapFileEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/files").WithTags("Files");

        // GET /api/v1/files/browse?path=/mnt/3dprinting
        // Server-side directory browser — returns folder/file listing for the given path.
        // Security: only paths within configured Storage:BasePaths or common mount points
        // (/mnt, /library, /data) are allowed. Path traversal is prevented by resolving
        // the canonical path before the allowlist check.
        group.MapGet("/browse", (
            [FromQuery] string? path,
            IConfiguration config) =>
        {
            // Build allowed roots from config + sensible defaults
            var configuredPaths = config.GetSection("Storage:BasePaths").Get<string[]>() ?? ["/mnt/3dprinting"];
            var allowedRoots = new List<string>(configuredPaths)
            {
                "/mnt",
                "/library",
                "/data",
            };

            // Default to first configured base path
            var requestedPath = path ?? configuredPaths[0];

            // Resolve the canonical (fully-expanded, symlink-resolved) path to prevent traversal
            string browsePath;
            try
            {
                browsePath = Path.GetFullPath(requestedPath);
            }
            catch
            {
                return Results.BadRequest(new { message = "Invalid path." });
            }

            // Security gate: canonical path must start with an allowed root
            var allowed = allowedRoots.Any(root =>
            {
                var canonical = Path.GetFullPath(root);
                return browsePath.StartsWith(canonical + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || browsePath.Equals(canonical, StringComparison.OrdinalIgnoreCase);
            });

            if (!allowed)
                return Results.Json(new { message = "Access denied. Path is outside allowed directories." }, statusCode: 403);

            if (!Directory.Exists(browsePath))
                return Results.NotFound(new { message = $"Directory not found: {browsePath}" });

            var entries = new List<object>();

            // Parent directory entry (unless we're at an allowed root)
            var parent = Path.GetDirectoryName(browsePath);
            if (parent != null)
            {
                var parentCanonical = Path.GetFullPath(parent);
                var parentAllowed = allowedRoots.Any(root =>
                {
                    var canonical = Path.GetFullPath(root);
                    return parentCanonical.StartsWith(canonical + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || parentCanonical.Equals(canonical, StringComparison.OrdinalIgnoreCase);
                });
                if (parentAllowed)
                {
                    entries.Add(new
                    {
                        name = "..",
                        path = parent,
                        type = "directory",
                        size = 0L,
                        itemCount = (int?)null,
                        modified = (DateTime?)null,
                    });
                }
            }

            // Directories first
            try
            {
                foreach (var dir in Directory.GetDirectories(browsePath).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        int? itemCount = null;
                        try { itemCount = Directory.GetFileSystemEntries(dir).Length; } catch { }

                        entries.Add(new
                        {
                            name = Path.GetFileName(dir),
                            path = dir,
                            type = "directory",
                            size = 0L,
                            itemCount,
                            modified = (DateTime?)Directory.GetLastWriteTimeUtc(dir),
                        });
                    }
                    catch { /* skip inaccessible */ }
                }
            }
            catch { /* skip if directory listing fails entirely */ }

            // Files
            try
            {
                foreach (var file in Directory.GetFiles(browsePath).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        entries.Add(new
                        {
                            name = info.Name,
                            path = file,
                            type = GetFileType(info.Extension),
                            size = info.Length,
                            itemCount = (int?)null,
                            modified = (DateTime?)info.LastWriteTimeUtc,
                        });
                    }
                    catch { /* skip inaccessible */ }
                }
            }
            catch { }

            return Results.Ok(new
            {
                currentPath = browsePath,
                entries,
                breadcrumbs = GetBreadcrumbs(browsePath, allowedRoots),
            });

        }).WithName("BrowseFiles");
    }

    /// <summary>Classify a file by its extension.</summary>
    private static string GetFileType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".stl" or ".obj" or ".3mf" or ".step" or ".stp" or ".iges" or ".igs" => "stl",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".lzma" => "archive",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".tiff" => "image",
            ".pdf" or ".txt" or ".md" or ".doc" or ".docx" => "document",
            _ => "other",
        };
    }

    /// <summary>
    /// Build breadcrumb segments for a given path, stopping at the first allowed root.
    /// e.g. /mnt/3dprinting/creators → [{name:"mnt",path:"/mnt"}, {name:"3dprinting",path:"/mnt/3dprinting"}, ...]
    /// </summary>
    private static List<object> GetBreadcrumbs(string fullPath, List<string> allowedRoots)
    {
        var crumbs = new List<object>();
        var parts = fullPath.TrimEnd(Path.DirectorySeparatorChar)
                            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        // Find the deepest allowed root that is a prefix of fullPath, to stop breadcrumbs above it
        var lowestAllowedDepth = 0;
        foreach (var root in allowedRoots)
        {
            try
            {
                var canonRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                var rootParts = canonRoot.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                if (fullPath.StartsWith(canonRoot, StringComparison.OrdinalIgnoreCase) && rootParts.Length > lowestAllowedDepth)
                    lowestAllowedDepth = rootParts.Length;
            }
            catch { }
        }

        // Build from the shallowest allowed ancestor (lowestAllowedDepth - 1 to show the root itself)
        var startDepth = Math.Max(lowestAllowedDepth - 1, 0);
        for (int i = startDepth; i < parts.Length; i++)
        {
            var segmentPath = "/" + string.Join("/", parts[..( i + 1)]);
            crumbs.Add(new { name = parts[i], path = segmentPath });
        }

        return crumbs;
    }
}
