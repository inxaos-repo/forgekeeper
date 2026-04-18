using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Repositories;
using Forgekeeper.Infrastructure.Services;
using Forgekeeper.Infrastructure.SourceAdapters;
using Forgekeeper.Api.BackgroundServices;
using Forgekeeper.Api.Endpoints;
using Forgekeeper.Api.Mcp;
using Forgekeeper.Api.Middleware;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog
builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console());

// Database — configure NpgsqlDataSource with dynamic JSON support for JSONB columns
var connectionString = builder.Configuration.GetConnectionString("ForgeDb");
var npgsqlDataSource = new Npgsql.NpgsqlDataSourceBuilder(connectionString)
    .EnableDynamicJson()
    .Build();

builder.Services.AddDbContextFactory<ForgeDbContext>(options =>
    options.UseNpgsql(npgsqlDataSource)
        .UseSnakeCaseNamingConvention()
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

builder.Services.AddDbContext<ForgeDbContext>(options =>
    options.UseNpgsql(npgsqlDataSource)
        .UseSnakeCaseNamingConvention()
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

// Repositories
builder.Services.AddScoped<IModelRepository, ModelRepository>();
builder.Services.AddScoped<ICreatorRepository, CreatorRepository>();

// Source Adapters
builder.Services.AddSingleton<ISourceAdapter, MmfSourceAdapter>();
builder.Services.AddSingleton<ISourceAdapter>(
    new GenericSourceAdapter(SourceType.Thangs, "thangs"));
builder.Services.AddSingleton<ISourceAdapter>(
    new GenericSourceAdapter(SourceType.Cults3d, "cults3d"));
builder.Services.AddSingleton<ISourceAdapter>(
    new GenericSourceAdapter(SourceType.Thingiverse, "thingiverse"));
builder.Services.AddSingleton<ISourceAdapter>(
    new GenericSourceAdapter(SourceType.Manual, "manual"));
builder.Services.AddSingleton<ISourceAdapter, PatreonSourceAdapter>();

// Services
builder.Services.AddSingleton<IMetadataService, MetadataService>();
builder.Services.AddSingleton<IScannerService, FileScannerService>();
builder.Services.AddScoped<ISearchService, SearchService>();
builder.Services.AddScoped<IImportService, ImportService>();
builder.Services.AddSingleton<IThumbnailService, ThumbnailService>();

// Background Services
builder.Services.AddHostedService<ThumbnailWorker>();
builder.Services.AddHostedService<ScannerWorker>();
builder.Services.AddSingleton<PluginHostService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PluginHostService>());

// CORS for Vue.js dev server
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://localhost:3000")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

// Apply migrations on startup (skip for InMemory/testing)
if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
    await db.Database.MigrateAsync();
}

app.UseCors();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseSerilogRequestLogging();

// Serve static files (Vue.js build output)
app.UseDefaultFiles();
app.UseStaticFiles();

// Health check
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// Prometheus metrics endpoint
app.MapGet("/metrics", async (IServiceProvider services, PluginHostService pluginHost, CancellationToken ct) =>
{
    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

    // Model counts by source
    var modelsBySource = await db.Models
        .GroupBy(m => m.Source)
        .Select(g => new { Source = g.Key, Count = g.Count() })
        .ToListAsync(ct);

    long mmfCount = modelsBySource.FirstOrDefault(x => x.Source == Forgekeeper.Core.Enums.SourceType.Mmf)?.Count ?? 0;
    long manualCount = modelsBySource.FirstOrDefault(x => x.Source == Forgekeeper.Core.Enums.SourceType.Manual)?.Count ?? 0;

    var totalCreators = await db.Creators.CountAsync(ct);
    var totalFiles = await db.Variants.CountAsync(ct);
    var totalSizeBytes = await db.Models.SumAsync(m => m.TotalSizeBytes, ct);
    var thumbnailCount = await db.Models.CountAsync(m => m.ThumbnailPath != null, ct);
    var printedCount = await db.Models.CountAsync(m => m.PrintHistory != null && m.PrintHistory.Count > 0, ct);

    // Known plugin slugs (all loaded plugins)
    var pluginSlugs = pluginHost.Plugins.Keys.ToList();

    var sb = new System.Text.StringBuilder();

    // Models by source
    sb.AppendLine("# HELP forgekeeper_models_total Total models in library");
    sb.AppendLine("# TYPE forgekeeper_models_total gauge");
    sb.AppendLine($"forgekeeper_models_total{{source=\"mmf\"}} {mmfCount}");
    sb.AppendLine($"forgekeeper_models_total{{source=\"manual\"}} {manualCount}");
    sb.AppendLine();

    // Creators
    sb.AppendLine("# HELP forgekeeper_creators_total Total creators");
    sb.AppendLine("# TYPE forgekeeper_creators_total gauge");
    sb.AppendLine($"forgekeeper_creators_total {totalCreators}");
    sb.AppendLine();

    // Files
    sb.AppendLine("# HELP forgekeeper_files_total Total file variants");
    sb.AppendLine("# TYPE forgekeeper_files_total gauge");
    sb.AppendLine($"forgekeeper_files_total {totalFiles}");
    sb.AppendLine();

    // Library size
    sb.AppendLine("# HELP forgekeeper_library_size_bytes Total library size in bytes");
    sb.AppendLine("# TYPE forgekeeper_library_size_bytes gauge");
    sb.AppendLine($"forgekeeper_library_size_bytes {totalSizeBytes}");
    sb.AppendLine();

    // Thumbnails
    sb.AppendLine("# HELP forgekeeper_thumbnails_total Generated thumbnails count");
    sb.AppendLine("# TYPE forgekeeper_thumbnails_total gauge");
    sb.AppendLine($"forgekeeper_thumbnails_total {thumbnailCount}");
    sb.AppendLine();

    // Printed
    sb.AppendLine("# HELP forgekeeper_printed_total Models with print status printed");
    sb.AppendLine("# TYPE forgekeeper_printed_total gauge");
    sb.AppendLine($"forgekeeper_printed_total {printedCount}");
    sb.AppendLine();

    // Per-plugin sync metrics
    sb.AppendLine("# HELP forgekeeper_sync_running Is a sync currently running (1=yes, 0=no)");
    sb.AppendLine("# TYPE forgekeeper_sync_running gauge");
    foreach (var slug in pluginSlugs)
    {
        var s = pluginHost.GetSyncStatus(slug);
        sb.AppendLine($"forgekeeper_sync_running{{plugin=\"{slug}\"}} {(s?.IsRunning == true ? 1 : 0)}");
    }
    sb.AppendLine();

    sb.AppendLine("# HELP forgekeeper_sync_scraped_total Models scraped in current/last sync");
    sb.AppendLine("# TYPE forgekeeper_sync_scraped_total gauge");
    foreach (var slug in pluginSlugs)
    {
        var s = pluginHost.GetSyncStatus(slug);
        sb.AppendLine($"forgekeeper_sync_scraped_total{{plugin=\"{slug}\"}} {s?.ScrapedModels ?? 0}");
    }
    sb.AppendLine();

    sb.AppendLine("# HELP forgekeeper_sync_failed_total Models failed in current/last sync");
    sb.AppendLine("# TYPE forgekeeper_sync_failed_total gauge");
    foreach (var slug in pluginSlugs)
    {
        var s = pluginHost.GetSyncStatus(slug);
        sb.AppendLine($"forgekeeper_sync_failed_total{{plugin=\"{slug}\"}} {s?.FailedModels ?? 0}");
    }
    sb.AppendLine();

    sb.AppendLine("# HELP forgekeeper_sync_total_models Total models in current sync manifest");
    sb.AppendLine("# TYPE forgekeeper_sync_total_models gauge");
    foreach (var slug in pluginSlugs)
    {
        var s = pluginHost.GetSyncStatus(slug);
        sb.AppendLine($"forgekeeper_sync_total_models{{plugin=\"{slug}\"}} {s?.TotalModels ?? 0}");
    }

    return Results.Text(sb.ToString(), "text/plain; version=0.0.4; charset=utf-8");
}).WithTags("Metrics").WithName("GetMetrics");

// Map API endpoints
app.MapModelEndpoints();
app.MapCreatorEndpoints();
app.MapTagEndpoints();
app.MapScanEndpoints();
app.MapImportEndpoints();
app.MapStatsEndpoints();
app.MapVariantEndpoints();
app.MapSourceEndpoints();
app.MapPluginEndpoints();

// --- MCP Endpoints ---
app.MapGet("/mcp/tools", () =>
    Results.Ok(ForgekeeperMcpServer.GetToolDefinitions()))
    .WithTags("MCP").WithName("McpListTools");

app.MapPost("/mcp/invoke", async (
    McpInvokeRequest request,
    IServiceProvider services,
    CancellationToken ct) =>
{
    var response = await ForgekeeperMcpServer.InvokeAsync(request, services, ct);
    return response.IsError ? Results.BadRequest(response) : Results.Ok(response);
}).WithTags("MCP").WithName("McpInvokeTool");

// SPA fallback — serve index.html for non-API, non-file routes
app.MapFallbackToFile("index.html");

app.Run();

// Needed for WebApplicationFactory<Program> in integration tests
public partial class Program { }
