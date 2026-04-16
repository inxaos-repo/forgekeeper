using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Forgekeeper.Api.Endpoints;

public static class SourceEndpoints
{
    public static void MapSourceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sources").WithTags("Sources");

        group.MapGet("/", async (ForgeDbContext db, CancellationToken ct) =>
        {
            var sources = await db.Sources
                .OrderBy(s => s.Name)
                .Select(s => new SourceResponse
                {
                    Id = s.Id,
                    Slug = s.Slug,
                    Name = s.Name,
                    BasePath = s.BasePath,
                    AdapterType = s.AdapterType,
                    AutoScan = s.AutoScan,
                    ModelCount = s.Models.Count,
                    CreatedAt = s.CreatedAt,
                    UpdatedAt = s.UpdatedAt,
                })
                .ToListAsync(ct);

            return Results.Ok(sources);
        }).WithName("ListSources");

        group.MapGet("/{slug}", async (
            string slug,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            var source = await db.Sources
                .Where(s => s.Slug == slug)
                .Select(s => new SourceResponse
                {
                    Id = s.Id,
                    Slug = s.Slug,
                    Name = s.Name,
                    BasePath = s.BasePath,
                    AdapterType = s.AdapterType,
                    AutoScan = s.AutoScan,
                    ModelCount = s.Models.Count,
                    CreatedAt = s.CreatedAt,
                    UpdatedAt = s.UpdatedAt,
                })
                .FirstOrDefaultAsync(ct);

            return source is null ? Results.NotFound() : Results.Ok(source);
        }).WithName("GetSource");

        group.MapPost("/", async (
            [FromBody] CreateSourceRequest request,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            if (await db.Sources.AnyAsync(s => s.Slug == request.Slug, ct))
                return Results.Conflict(new { message = $"Source with slug '{request.Slug}' already exists" });

            var source = new Source
            {
                Id = Guid.NewGuid(),
                Slug = request.Slug,
                Name = request.Name,
                BasePath = request.BasePath,
                AdapterType = request.AdapterType ?? "GenericSourceAdapter",
                AutoScan = request.AutoScan ?? true,
            };

            db.Sources.Add(source);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/sources/{source.Slug}", new SourceResponse
            {
                Id = source.Id,
                Slug = source.Slug,
                Name = source.Name,
                BasePath = source.BasePath,
                AdapterType = source.AdapterType,
                AutoScan = source.AutoScan,
                ModelCount = 0,
                CreatedAt = source.CreatedAt,
                UpdatedAt = source.UpdatedAt,
            });
        }).WithName("CreateSource");

        group.MapPatch("/{slug}", async (
            string slug,
            [FromBody] UpdateSourceRequest request,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            var source = await db.Sources.FirstOrDefaultAsync(s => s.Slug == slug, ct);
            if (source is null) return Results.NotFound();

            if (request.Name != null) source.Name = request.Name;
            if (request.BasePath != null) source.BasePath = request.BasePath;
            if (request.AdapterType != null) source.AdapterType = request.AdapterType;
            if (request.AutoScan.HasValue) source.AutoScan = request.AutoScan.Value;
            source.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
            return Results.Ok();
        }).WithName("UpdateSource");

        group.MapDelete("/{slug}", async (
            string slug,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            var source = await db.Sources.FirstOrDefaultAsync(s => s.Slug == slug, ct);
            if (source is null) return Results.NotFound();

            db.Sources.Remove(source);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("DeleteSource");
    }
}

public class SourceResponse
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BasePath { get; set; } = string.Empty;
    public string AdapterType { get; set; } = string.Empty;
    public bool AutoScan { get; set; }
    public int ModelCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class CreateSourceRequest
{
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BasePath { get; set; } = string.Empty;
    public string? AdapterType { get; set; }
    public bool? AutoScan { get; set; }
}

public class UpdateSourceRequest
{
    public string? Name { get; set; }
    public string? BasePath { get; set; }
    public string? AdapterType { get; set; }
    public bool? AutoScan { get; set; }
}
