using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Repositories;
using Forgekeeper.Infrastructure.Services;
using Forgekeeper.Infrastructure.SourceAdapters;
using Forgekeeper.Api.BackgroundServices;
using Forgekeeper.Api.Endpoints;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog
builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console());

// Database
builder.Services.AddDbContextFactory<ForgeDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("ForgeDb")));

builder.Services.AddDbContext<ForgeDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("ForgeDb")));

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

// Apply migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
    await db.Database.MigrateAsync();
}

app.UseCors();
app.UseSerilogRequestLogging();

// Serve static files (Vue.js build output)
app.UseDefaultFiles();
app.UseStaticFiles();

// Health check
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// Map API endpoints
app.MapModelEndpoints();
app.MapCreatorEndpoints();
app.MapTagEndpoints();
app.MapScanEndpoints();
app.MapImportEndpoints();
app.MapStatsEndpoints();
app.MapVariantEndpoints();
app.MapSourceEndpoints();

// SPA fallback — serve index.html for non-API, non-file routes
app.MapFallbackToFile("index.html");

app.Run();
