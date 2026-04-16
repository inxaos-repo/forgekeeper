using Forgekeeper.Core.DTOs;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Forgekeeper.Api.Endpoints;

public static class StatsEndpoints
{
    public static void MapStatsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/stats").WithTags("Stats");

        group.MapGet("/", async (
            IModelRepository repo,
            CancellationToken ct) =>
        {
            var stats = await repo.GetStatsAsync(ct);
            return Results.Ok(stats);
        }).WithName("GetStats");

        group.MapGet("/creators", async (
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            var stats = await db.Creators
                .OrderByDescending(c => c.ModelCount)
                .Select(c => new CreatorStatsItem
                {
                    Id = c.Id,
                    Name = c.Name,
                    ModelCount = c.ModelCount,
                    TotalSizeBytes = c.Models.Sum(m => m.TotalSizeBytes)
                })
                .ToListAsync(ct);

            return Results.Ok(stats);
        }).WithName("GetCreatorStats");
    }
}
