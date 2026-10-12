using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Forgekeeper.Api.Endpoints;

public static class VariantEndpoints
{
    public static void MapVariantEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/variants").WithTags("Variants");

        // GET /api/v1/variants/{id}/download[?inline=true]
        // Streams the file (range requests supported). inline=true omits the attachment filename so the
        // browser opens it in a tab when it can (images, PDF); otherwise it downloads.
        // The resolved path must stay inside the model folder AND a configured library root (no traversal).
        group.MapGet("/{id:guid}/download", async (
            Guid id,
            bool? inline,
            ForgeDbContext db,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var variant = await db.Variants
                .Include(v => v.Model)
                .FirstOrDefaultAsync(v => v.Id == id, ct);

            if (variant == null) return Results.NotFound();

            var filePath = Path.GetFullPath(Path.Combine(variant.Model.BasePath, variant.FilePath));
            var lib = LibraryPathSettings.From(config);
            if (!LibraryPathMapper.IsWithinRoots(filePath, [variant.Model.BasePath])
                || !LibraryPathMapper.IsWithinRoots(filePath, lib.AllowedRoots))
                return Results.Json(new { message = "File is outside the library." }, statusCode: 403);
            if (!File.Exists(filePath)) return Results.NotFound("File not found on disk");

            var contentType = ContentTypeFor(variant.FileType, variant.FileName);
            return inline == true
                ? Results.File(filePath, contentType, enableRangeProcessing: true)
                : Results.File(filePath, contentType, variant.FileName, enableRangeProcessing: true);
        }).WithName("DownloadVariant");

        group.MapGet("/{id:guid}/thumbnail", async (
            Guid id,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            var variant = await db.Variants.FindAsync([id], ct);
            if (variant?.ThumbnailPath == null) return Results.NotFound();

            if (!File.Exists(variant.ThumbnailPath))
                return Results.NotFound();

            return Results.File(variant.ThumbnailPath, "image/webp");
        }).WithName("GetVariantThumbnail");
    }

    internal static string ContentTypeFor(Core.Enums.FileType type, string fileName) => type switch
    {
        Core.Enums.FileType.Stl => "model/stl",
        Core.Enums.FileType.Obj => "model/obj",
        Core.Enums.FileType.Threemf => "model/3mf",
        _ => Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".txt" or ".md" => "text/plain; charset=utf-8",
            _ => "application/octet-stream",
        },
    };
}
