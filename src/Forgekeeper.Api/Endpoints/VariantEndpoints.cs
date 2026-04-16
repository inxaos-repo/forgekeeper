using Forgekeeper.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Forgekeeper.Api.Endpoints;

public static class VariantEndpoints
{
    public static void MapVariantEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/variants").WithTags("Variants");

        group.MapGet("/{id:guid}/download", async (
            Guid id,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            var variant = await db.Variants
                .Include(v => v.Model)
                .FirstOrDefaultAsync(v => v.Id == id, ct);

            if (variant == null) return Results.NotFound();

            var filePath = Path.Combine(variant.Model.BasePath, variant.FilePath);
            if (!File.Exists(filePath)) return Results.NotFound("File not found on disk");

            var contentType = variant.FileType switch
            {
                Core.Enums.FileType.Stl => "model/stl",
                Core.Enums.FileType.Obj => "model/obj",
                Core.Enums.FileType.Threemf => "model/3mf",
                _ => "application/octet-stream"
            };

            return Results.File(filePath, contentType, variant.FileName);
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
}
