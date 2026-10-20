using System.IO.Compression;
using System.Text;
using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Services;
using Forgekeeper.Infrastructure.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Forgekeeper.Api.Endpoints;

/// <summary>
/// "Download all as ZIP" for a model, optionally restricted to one variant type
/// (supported, presupported, ...). The archive is streamed straight to the
/// response body: no temp files, nothing buffered in memory beyond a small
/// copy buffer. Files go through the same library-root and traversal checks
/// as <c>GET /api/v1/variants/{id}/download</c>.
/// </summary>
public static class ModelZipEndpoints
{
    private const string MissingFilesEntry = "_MISSING_FILES.txt";

    // Formats that are already compressed; deflating them only burns CPU.
    private static readonly HashSet<string> StoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".gz", ".3mf", ".jpg", ".jpeg", ".png", ".webp", ".gif",
        ".mp4", ".pdf", ".ctb", ".cbddlp", ".pwmx", ".pwma", ".goo", ".lys", ".chitubox",
    };

    public static void MapModelZipEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/models/{id:guid}/download.zip", DownloadZip)
            .WithName("DownloadModelZip")
            .WithTags("Models");
    }

    internal sealed record ZipItem(string AbsolutePath, string EntryName);

    private static async Task<IResult> DownloadZip(
        Guid id,
        string? variantType,
        HttpContext http,
        ForgeDbContext db,
        IConfiguration config,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Forgekeeper.ModelZip");

        VariantType? filter = null;
        if (!string.IsNullOrWhiteSpace(variantType))
        {
            if (!Enum.TryParse<VariantType>(variantType, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed) || int.TryParse(variantType, out _))
                return Results.BadRequest(new { error = $"Unknown variantType '{variantType}'" });
            filter = parsed;
        }

        var model = await db.Models
            .AsNoTracking()
            .Include(m => m.Creator)
            .Include(m => m.Variants)
            .FirstOrDefaultAsync(m => m.Id == id, ct);
        if (model is null)
            return Results.NotFound();

        var variants = model.Variants
            .Where(v => filter is null || v.VariantType == filter)
            .OrderBy(v => v.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (variants.Count == 0)
            return Results.NotFound(new { error = "No files for this model/variant type" });

        var roots = LibraryPathSettings.From(config).AllowedRoots;
        if (!LibraryPathMapper.IsWithinRoots(model.BasePath, roots))
            return Results.Json(new { message = "Model is outside the library." }, statusCode: 403);

        var baseFull = Path.GetFullPath(model.BasePath);
        var items = new List<ZipItem>();
        var missing = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MissingFilesEntry };

        foreach (var v in variants)
        {
            var abs = Path.GetFullPath(Path.Combine(model.BasePath, v.FilePath));
            // Same rules as DownloadVariant: must stay inside the model folder
            // and inside a configured library root.
            if (!LibraryPathMapper.IsWithinRoots(abs, [model.BasePath]) || !LibraryPathMapper.IsWithinRoots(abs, roots))
            {
                logger.LogWarning("ZIP {ModelId}: skipping {Path} (outside model folder/library roots)", id, v.FilePath);
                missing.Add($"{v.FilePath} (refused: outside model folder)");
                continue;
            }
            if (!File.Exists(abs))
            {
                missing.Add($"{v.FilePath} (not found on disk)");
                continue;
            }
            items.Add(new ZipItem(abs, UniqueName(EntryNameFor(abs, baseFull, v.FileName), usedNames)));
        }

        if (items.Count == 0)
            return Results.NotFound(new { error = "None of the files exist on disk", missing });

        var fileName = ZipFileName(model.Creator?.Name, model.Name, filter);
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/zip";
        var cd = new ContentDispositionHeaderValue("attachment");
        cd.SetHttpFileName(fileName);
        http.Response.Headers[HeaderNames.ContentDisposition] = cd.ToString();
        http.Response.Headers[HeaderNames.CacheControl] = "no-store";

        // ZipArchive (.NET 9) only has a synchronous API; allow sync IO for this
        // response and put a buffer in front so Kestrel sees large writes.
        var bodyControl = http.Features.Get<IHttpBodyControlFeature>();
        if (bodyControl is not null) bodyControl.AllowSynchronousIO = true;
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await http.Response.StartAsync(ct);
        await using (var buffered = new BufferedStream(http.Response.Body, 256 * 1024))
        {
            WriteZip(buffered, items, missing, logger, id, ct);
            await buffered.FlushAsync(ct);
        }
        return Results.Empty;
    }

    internal static void WriteZip(Stream output, IReadOnlyList<ZipItem> items, List<string> missing,
        ILogger logger, Guid modelId, CancellationToken ct)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        var buffer = new byte[128 * 1024];
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            FileStream src;
            try
            {
                src = new FileStream(item.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Vanished/unreadable between the existence check and now.
                logger.LogWarning(ex, "ZIP {ModelId}: cannot open {Path}", modelId, item.AbsolutePath);
                missing.Add($"{item.EntryName} (could not be read)");
                continue;
            }

            using (src)
            {
                var level = StoredExtensions.Contains(Path.GetExtension(item.EntryName))
                    ? CompressionLevel.NoCompression
                    : CompressionLevel.Fastest;
                var entry = zip.CreateEntry(item.EntryName, level);
                try { entry.LastWriteTime = File.GetLastWriteTime(item.AbsolutePath); } catch { /* keep default */ }
                using var dst = entry.Open();
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    dst.Write(buffer, 0, read);
                }
            }
        }

        if (missing.Count > 0)
        {
            var entry = zip.CreateEntry(MissingFilesEntry, CompressionLevel.Fastest);
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.WriteLine("These files are listed in Forgekeeper but were not included in this ZIP:");
            foreach (var m in missing) w.WriteLine(m);
        }
    }

    /// <summary>Path relative to the model folder, with '/' separators (keeps sub-folders).</summary>
    public static string EntryNameFor(string absolutePath, string baseFull, string fallbackName)
    {
        var rel = Path.GetRelativePath(baseFull, absolutePath).Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(rel) || rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
            rel = Path.GetFileName(fallbackName);
        return rel.TrimStart('/');
    }

    public static string UniqueName(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        var dir = name.Contains('/') ? name[..(name.LastIndexOf('/') + 1)] : "";
        var file = name[dir.Length..];
        var stem = Path.GetFileNameWithoutExtension(file);
        var ext = Path.GetExtension(file);
        for (var i = 2; ; i++)
        {
            var candidate = $"{dir}{stem} ({i}){ext}";
            if (used.Add(candidate)) return candidate;
        }
    }

    public static string ZipFileName(string? creator, string modelName, VariantType? type)
    {
        var name = string.IsNullOrWhiteSpace(creator) ? modelName : $"{creator} - {modelName}";
        if (type is not null) name += $" ({type})";
        var sb = new StringBuilder(name.Length);
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '"', '\\', '/', ':', '*', '?', '<', '>', '|' }).ToHashSet();
        foreach (var c in name)
            sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        var cleaned = sb.ToString().Trim().Trim('.');
        if (cleaned.Length > 150) cleaned = cleaned[..150].TrimEnd();
        if (cleaned.Length == 0) cleaned = "model";
        return cleaned + ".zip";
    }
}
