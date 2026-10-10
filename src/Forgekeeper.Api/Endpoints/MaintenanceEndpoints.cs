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

        group.MapPost("/mmf-orphans/merge", async (
            MmfOrphanMergeService svc,
            [FromQuery] bool? apply,
            [FromQuery] string? reportDir,
            CancellationToken ct) =>
        {
            var report = await svc.MergeOrphansAsync(apply == true, ct);
            string? path = null;
            try { path = await MmfOrphanMergeService.WriteReportAsync(reportDir ?? DefaultReportDir, apply == true ? "mmf-orphan-merge-applied" : "mmf-orphan-merge-dryrun", report, ct); }
            catch (Exception) { /* report dir may be unavailable in dev/tests */ }
            return Results.Ok(new
            {
                report.Applied, report.Orphans, report.Pairs, report.Ambiguous, report.Unmatched, report.Merged,
                reportPath = path,
                ambiguousSample = report.AmbiguousList.Take(50),
                pairSample = report.PairList.Take(20),
            });
        }).WithName("MergeMmfOrphans");

        group.MapPost("/source/backfill", async (
            MmfOrphanMergeService svc,
            [FromQuery] bool? apply,
            CancellationToken ct) => Results.Ok(await svc.BackfillSourceAsync(apply == true, ct)))
            .WithName("BackfillModelSource");
    }
}
