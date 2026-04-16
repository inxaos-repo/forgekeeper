using Forgekeeper.Core.DTOs;
using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Forgekeeper.Api.Endpoints;

public static class ImportEndpoints
{
    public static void MapImportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/import").WithTags("Import");

        group.MapPost("/process", async (
            IImportService importService,
            CancellationToken ct) =>
        {
            var results = await importService.ProcessUnsortedAsync(ct);
            return Results.Ok(results);
        }).WithName("ProcessUnsorted");

        group.MapGet("/queue", async (
            [FromQuery] ImportStatus? status,
            IImportService importService,
            CancellationToken ct) =>
        {
            var items = await importService.GetQueueAsync(status, ct);
            return Results.Ok(items);
        }).WithName("GetImportQueue");

        group.MapPost("/queue/{id:guid}/confirm", async (
            Guid id,
            [FromBody] ImportConfirmRequest request,
            IImportService importService,
            CancellationToken ct) =>
        {
            try
            {
                await importService.ConfirmImportAsync(id, request, ct);
                return Results.Ok(new { message = "Import confirmed" });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        }).WithName("ConfirmImport");

        group.MapDelete("/queue/{id:guid}", async (
            Guid id,
            IImportService importService,
            CancellationToken ct) =>
        {
            try
            {
                await importService.DismissAsync(id, ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        }).WithName("DismissImport");
    }
}
