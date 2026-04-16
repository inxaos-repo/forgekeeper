using Forgekeeper.Core.Interfaces;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Forgekeeper.Api.Endpoints;

public static class TagEndpoints
{
    public static void MapTagEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1").WithTags("Tags");

        group.MapGet("/tags", async (ForgeDbContext db, CancellationToken ct) =>
        {
            var tags = await db.Tags
                .OrderBy(t => t.Name)
                .Select(t => new { t.Id, t.Name, ModelCount = t.Models.Count })
                .ToListAsync(ct);
            return Results.Ok(tags);
        }).WithName("ListTags");

        group.MapPost("/models/{modelId:guid}/tags", async (
            Guid modelId,
            [FromBody] TagRequest request,
            IModelRepository repo,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            var model = await repo.GetByIdAsync(modelId, ct);
            if (model == null) return Results.NotFound();

            foreach (var tagName in request.Tags)
            {
                var normalized = tagName.ToLowerInvariant().Trim();
                if (string.IsNullOrEmpty(normalized)) continue;

                var tag = await db.Tags.FirstOrDefaultAsync(t => t.Name == normalized, ct);
                if (tag == null)
                {
                    tag = new Tag { Id = Guid.NewGuid(), Name = normalized };
                    db.Tags.Add(tag);
                }

                if (!model.Tags.Any(t => t.Name == normalized))
                    model.Tags.Add(tag);
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(model.Tags.Select(t => t.Name));
        }).WithName("AddTags");

        group.MapDelete("/models/{modelId:guid}/tags/{tagName}", async (
            Guid modelId,
            string tagName,
            IModelRepository repo,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            var model = await repo.GetByIdAsync(modelId, ct);
            if (model == null) return Results.NotFound();

            var normalized = tagName.ToLowerInvariant().Trim();
            var tag = model.Tags.FirstOrDefault(t => t.Name == normalized);
            if (tag != null)
            {
                model.Tags.Remove(tag);
                await db.SaveChangesAsync(ct);
            }

            return Results.NoContent();
        }).WithName("RemoveTag");
    }
}

public record TagRequest(List<string> Tags);
