using Forgekeeper.Core.Interfaces;

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
    }
}
