using Forgekeeper.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace Forgekeeper.Api.Endpoints;

/// <summary>Library repair operations (#63, #67). All default to dry-run; pass apply=true to write.</summary>
public static class MaintenanceEndpoints
{
    private const string DefaultReportDir = "/library/sources/mmf/.forgekeeper-reports";

    public static void MapMaintenanceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/maintenance").WithTags("Maintenance");

        // #73: runs as a background job (the 60s ingress timeout used to cancel it through RequestAborted).
        // Poll GET /mmf-orphans/merge/status. Refuses (409) while a scan, a plugin sync or another job runs.
        group.MapPost("/mmf-orphans/merge", (
            MmfOrphanMergeJob job,
            IHostApplicationLifetime lifetime,
            [FromQuery] bool? apply,
            [FromQuery] bool? includeLive,
            [FromQuery] string? reportDir) =>
        {
            var (started, reason, _) = job.TryStart(apply == true, includeLive == true, reportDir ?? DefaultReportDir,
                lifetime.ApplicationStopping);
            if (!started)
                return Results.Conflict(new { message = $"Orphan merge refused: {reason}", status = job.Status });
            return Results.Accepted("/api/v1/maintenance/mmf-orphans/merge/status",
                new { message = "Orphan merge started", statusUrl = "/api/v1/maintenance/mmf-orphans/merge/status", status = job.Status });
        }).WithName("MergeMmfOrphans");

        group.MapGet("/mmf-orphans/merge/status", (MmfOrphanMergeJob job) =>
            Results.Ok(new { job.Status, blockedBy = job.BlockReason() }))
            .WithName("MergeMmfOrphansStatus");

        group.MapPost("/source/backfill", async (
            MmfOrphanMergeService svc,
            [FromQuery] bool? apply,
            CancellationToken ct) => Results.Ok(await svc.BackfillSourceAsync(apply == true, ct)))
            .WithName("BackfillModelSource");
    }
}
