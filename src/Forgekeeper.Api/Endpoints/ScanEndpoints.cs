using Forgekeeper.Core.Interfaces;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Forgekeeper.Api.Endpoints;

public static class ScanEndpoints
{
    public static void MapScanEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/scan").WithTags("Scanner");

        group.MapPost("/", async (IScannerService scanner, CancellationToken ct) =>
        {
            if (scanner.IsRunning)
                return Results.Conflict(new { message = "Scan already running", progress = scanner.GetProgress() });

            // Fire and forget — client polls /status
            _ = scanner.ScanAsync(incremental: false, ct);

            return Results.Accepted(value: new { message = "Full scan started", progress = scanner.GetProgress() });
        }).WithName("StartFullScan");

        group.MapPost("/incremental", async (IScannerService scanner, CancellationToken ct) =>
        {
            if (scanner.IsRunning)
                return Results.Conflict(new { message = "Scan already running", progress = scanner.GetProgress() });

            _ = scanner.ScanAsync(incremental: true, ct);

            return Results.Accepted(value: new { message = "Incremental scan started", progress = scanner.GetProgress() });
        }).WithName("StartIncrementalScan");

        group.MapGet("/status", (IScannerService scanner) =>
        {
            return Results.Ok(scanner.GetProgress());
        }).WithName("GetScanStatus");

        group.MapGet("/untracked", async (IScannerService scanner, [FromQuery] string? source, CancellationToken ct) =>
            Results.Ok(await scanner.FindUntrackedFilesAsync(source, ct)))
            .WithName("GetUntrackedFiles");

        // POST /api/v1/scan/verify — integrity check: verify models and files exist on disk
        // GET /api/v1/scan/health — combined library health report
        group.MapGet("/health", async (
            ForgeDbContext db,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var basePaths = config.GetSection("Storage:BasePaths").Get<string[]>() ?? ["/mnt/3dprinting"];

            var totalModels = await db.Models.CountAsync(ct);
            var totalFiles = await db.Variants.CountAsync(ct);
            var zeroFileModels = await db.Models.CountAsync(m => m.FileCount == 0, ct);
            var unknownCreatorModels = await db.Models
                .CountAsync(m => m.Creator.Name == "unknown", ct);

            // Push GroupBy to SQL — no in-memory aggregation at scale
            var knownWithFiles    = await db.Models.CountAsync(m => m.Creator.Name != "unknown" && m.FileCount > 0,  ct);
            var knownWithoutFiles = await db.Models.CountAsync(m => m.Creator.Name != "unknown" && m.FileCount == 0, ct);
            var unknownWithFiles    = await db.Models.CountAsync(m => m.Creator.Name == "unknown" && m.FileCount > 0,  ct);
            var unknownWithoutFiles = await db.Models.CountAsync(m => m.Creator.Name == "unknown" && m.FileCount == 0, ct);

            var creatorBreakdown = new List<object>
            {
                new { Status = "known",   Count = knownWithFiles   + knownWithoutFiles,   WithFiles = knownWithFiles,   WithoutFiles = knownWithoutFiles   },
                new { Status = "unknown", Count = unknownWithFiles + unknownWithoutFiles, WithFiles = unknownWithFiles, WithoutFiles = unknownWithoutFiles },
            };

            var totalSizeBytes = await db.Models.SumAsync(m => m.TotalSizeBytes, ct);

            return Results.Ok(new
            {
                totalModels,
                totalFiles,
                totalSizeBytes,
                zeroFileModels,
                unknownCreatorModels,
                modelsWithFiles = totalModels - zeroFileModels,
                creatorBreakdown,
                downloadCompletionPercent = totalModels > 0
                    ? Math.Round((double)(totalModels - zeroFileModels) / totalModels * 100, 1)
                    : 0,
            });
        }).WithName("GetLibraryHealth");

        group.MapPost("/verify", async (ForgeDbContext db, CancellationToken ct) =>
        {
            // Process in batches of 500 to avoid loading the entire library into memory.
            // At 100K models × 10 variants = 1M rows — this would OOM without batching.
            const int BatchSize = 500;

            int totalModels = 0, verifiedModels = 0, missingModels = 0;
            int totalFiles = 0, verifiedFiles = 0, missingFiles = 0;
            var missingItems = new List<object>();

            var modelCount = await db.Models.CountAsync(ct);
            int offset = 0;

            while (offset < modelCount)
            {
                ct.ThrowIfCancellationRequested();

                var batch = await db.Models
                    .Include(m => m.Variants)
                    .OrderBy(m => m.Id)
                    .Skip(offset)
                    .Take(BatchSize)
                    .AsNoTracking()
                    .ToListAsync(ct);

                foreach (var model in batch)
                {
                    totalModels++;
                    bool modelDirExists = Directory.Exists(model.BasePath);
                    if (modelDirExists)
                        verifiedModels++;
                    else
                    {
                        missingModels++;
                        missingItems.Add(new
                        {
                            type = "model",
                            modelId = model.Id,
                            modelName = model.Name,
                            path = model.BasePath,
                        });
                    }

                    foreach (var variant in model.Variants)
                    {
                        totalFiles++;
                        if (File.Exists(variant.FilePath))
                            verifiedFiles++;
                        else
                        {
                            missingFiles++;
                            missingItems.Add(new
                            {
                                type = "file",
                                modelId = model.Id,
                                modelName = model.Name,
                                variantId = variant.Id,
                                fileName = variant.FileName,
                                path = variant.FilePath,
                            });
                        }
                    }
                }

                offset += BatchSize;
            }

            return Results.Ok(new
            {
                totalModels,
                verifiedModels,
                missingModels,
                totalFiles,
                verifiedFiles,
                missingFiles,
                missingItems,
            });
        }).WithName("VerifyIntegrity");

        // ── File Issue Tracking ──────────────────────────────────────────────

        // GET /api/v1/scan/issues — list active issues (paginated, filterable by type)
        group.MapGet("/issues", async (
            FileIssueService fileIssueService,
            [FromQuery] string? type,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            CancellationToken ct = default) =>
        {
            var issues = await fileIssueService.GetActiveIssuesAsync(type, page, pageSize, ct);
            return Results.Ok(issues);
        }).WithName("GetFileIssues");

        // GET /api/v1/scan/issues/summary — counts by type
        group.MapGet("/issues/summary", async (
            FileIssueService fileIssueService,
            CancellationToken ct) =>
        {
            var summary = await fileIssueService.GetSummaryAsync(ct);
            return Results.Ok(summary);
        }).WithName("GetFileIssuesSummary");

        // POST /api/v1/scan/issues/{id}/dismiss — dismiss a single issue
        group.MapPost("/issues/{id}/dismiss", async (
            Guid id,
            FileIssueService fileIssueService,
            CancellationToken ct) =>
        {
            await fileIssueService.DismissAsync(id, "user", ct);
            return Results.NoContent();
        }).WithName("DismissFileIssue");

        // DELETE /api/v1/scan/issues/dismissed — purge all dismissed issues
        group.MapDelete("/issues/dismissed", async (
            FileIssueService fileIssueService,
            CancellationToken ct) =>
        {
            var deleted = await fileIssueService.PurgeDismissedAsync(ct);
            return Results.Ok(new { deleted });
        }).WithName("PurgeDismissedFileIssues");

        // GET /api/v1/scan/hash-status — SHA-256 hashing progress
        group.MapGet("/hash-status", async (ForgeDbContext db, CancellationToken ct) =>
        {
            var total = await db.Variants.CountAsync(ct);
            var hashed = await db.Variants.CountAsync(v => v.FileHash != null && v.FileHash != "sha256:missing", ct);
            var missing = await db.Variants.CountAsync(v => v.FileHash == "sha256:missing", ct);
            var unhashed = total - hashed - missing;

            return Results.Ok(new
            {
                totalFiles = total,
                hashedFiles = hashed,
                unhashedFiles = unhashed,
                missingFiles = missing,
                completionPercent = total > 0 ? Math.Round((double)hashed / total * 100, 1) : 0,
            });
        }).WithName("GetHashStatus");
    }
}
