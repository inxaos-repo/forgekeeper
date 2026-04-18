using Forgekeeper.Core.Interfaces;
using Forgekeeper.Infrastructure.Data;
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
                .Include(m => m.Creator)
                .CountAsync(m => m.Creator.Name == "unknown", ct);

            // Models by creator status (in-memory groupby for EF Core compat)
            var creatorData = await db.Models
                .Include(m => m.Creator)
                .Select(m => new { CreatorName = m.Creator.Name, m.FileCount })
                .ToListAsync(ct);

            var creatorBreakdown = creatorData
                .GroupBy(m => m.CreatorName == "unknown" ? "unknown" : "known")
                .Select(g => new
                {
                    Status = g.Key,
                    Count = g.Count(),
                    WithFiles = g.Count(m => m.FileCount > 0),
                    WithoutFiles = g.Count(m => m.FileCount == 0),
                })
                .ToList();

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
            var models = await db.Models
                .Include(m => m.Variants)
                .ToListAsync(ct);

            int totalModels = 0, verifiedModels = 0, missingModels = 0;
            int totalFiles = 0, verifiedFiles = 0, missingFiles = 0;
            var missingItems = new List<object>();

            foreach (var model in models)
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
    }
}
