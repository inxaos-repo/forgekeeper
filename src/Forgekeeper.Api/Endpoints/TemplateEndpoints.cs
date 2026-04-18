using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Forgekeeper.Api.Endpoints;

public static class TemplateEndpoints
{
    public static void MapTemplateEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/templates").WithTags("Templates");

        // GET /api/v1/templates — list all saved templates
        group.MapGet("/", async (
            [FromQuery] string? type,
            [FromQuery] string? creator,
            [FromQuery] string? source,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            var query = db.SavedTemplates.AsQueryable();
            if (!string.IsNullOrEmpty(type))
                query = query.Where(t => t.Type == type);
            if (!string.IsNullOrEmpty(creator))
                query = query.Where(t => t.CreatorName != null && t.CreatorName.ToLower() == creator.ToLower());
            if (!string.IsNullOrEmpty(source))
                query = query.Where(t => t.SourceSlug == source);

            var templates = await query
                .OrderByDescending(t => t.LastUsedAt ?? t.CreatedAt)
                .ToListAsync(ct);
            return Results.Ok(templates);
        }).WithName("ListTemplates");

        // GET /api/v1/templates/{id} — get a single template
        group.MapGet("/{id:guid}", async (Guid id, ForgeDbContext db, CancellationToken ct) =>
        {
            var template = await db.SavedTemplates.FindAsync([id], ct);
            return template != null ? Results.Ok(template) : Results.NotFound();
        }).WithName("GetTemplate");

        // POST /api/v1/templates — create a new saved template
        group.MapPost("/", async (
            [FromBody] SavedTemplateRequest request,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            var template = new SavedTemplate
            {
                Id = Guid.NewGuid(),
                Name = request.Name,
                Template = request.Template,
                Type = request.Type ?? "parse",
                CreatorName = request.CreatorName,
                SourceSlug = request.SourceSlug,
                Description = request.Description,
            };
            db.SavedTemplates.Add(template);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/templates/{template.Id}", template);
        }).WithName("CreateTemplate");

        // PATCH /api/v1/templates/{id} — update a template
        group.MapPatch("/{id:guid}", async (
            Guid id,
            [FromBody] SavedTemplateRequest request,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            var template = await db.SavedTemplates.FindAsync([id], ct);
            if (template == null) return Results.NotFound();

            if (request.Name != null) template.Name = request.Name;
            if (request.Template != null) template.Template = request.Template;
            if (request.Type != null) template.Type = request.Type;
            if (request.CreatorName != null) template.CreatorName = request.CreatorName;
            if (request.SourceSlug != null) template.SourceSlug = request.SourceSlug;
            if (request.Description != null) template.Description = request.Description;
            template.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
            return Results.Ok(template);
        }).WithName("UpdateTemplate");

        // DELETE /api/v1/templates/{id} — delete a template
        group.MapDelete("/{id:guid}", async (Guid id, ForgeDbContext db, CancellationToken ct) =>
        {
            var template = await db.SavedTemplates.FindAsync([id], ct);
            if (template == null) return Results.NotFound();
            db.SavedTemplates.Remove(template);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("DeleteTemplate");

        // POST /api/v1/templates/{id}/use — mark a template as used (increment counter)
        group.MapPost("/{id:guid}/use", async (Guid id, ForgeDbContext db, CancellationToken ct) =>
        {
            var template = await db.SavedTemplates.FindAsync([id], ct);
            if (template == null) return Results.NotFound();
            template.UseCount++;
            template.LastUsedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(template);
        }).WithName("UseTemplate");
    }
}

public class SavedTemplateRequest
{
    public string? Name { get; set; }
    public string? Template { get; set; }
    public string? Type { get; set; }
    public string? CreatorName { get; set; }
    public string? SourceSlug { get; set; }
    public string? Description { get; set; }
}
