using System.Collections.Concurrent;
using System.Diagnostics;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Infrastructure.Services;

public class ThumbnailService : IThumbnailService
{
    private readonly IDbContextFactory<ForgeDbContext> _dbFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ThumbnailService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentQueue<Guid> _priorityQueue = new();
    private bool? _rendererAvailable;

    public ThumbnailService(
        IDbContextFactory<ForgeDbContext> dbFactory,
        IConfiguration config,
        ILogger<ThumbnailService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _dbFactory = dbFactory;
        _config = config;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Enqueue a specific variant for priority thumbnail generation.
    /// </summary>
    public void EnqueueVariant(Guid variantId) => _priorityQueue.Enqueue(variantId);

    /// <summary>
    /// Check whether the configured thumbnail renderer (stl-thumb) is available on PATH.
    /// Result is cached after first check.
    /// </summary>
    public bool IsRendererAvailable()
    {
        if (_rendererAvailable.HasValue)
            return _rendererAvailable.Value;

        var renderer = _config.GetValue("Thumbnails:Renderer", "stl-thumb")!;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = renderer,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process == null)
            {
                _rendererAvailable = false;
            }
            else
            {
                process.WaitForExit(5000);
                _rendererAvailable = process.ExitCode == 0;
            }
        }
        catch
        {
            _rendererAvailable = false;
        }

        if (!_rendererAvailable.Value)
            _logger.LogWarning(
                "Thumbnail renderer '{Renderer}' not found on PATH. Thumbnail generation will be skipped. " +
                "Install stl-thumb: https://github.com/unlimitedbacon/stl-thumb",
                renderer);
        else
            _logger.LogInformation("Thumbnail renderer '{Renderer}' is available", renderer);

        return _rendererAvailable.Value;
    }

    public async Task GenerateThumbnailAsync(string stlPath, string outputPath, CancellationToken ct = default)
    {
        if (!IsRendererAvailable())
            return;

        var renderer = _config.GetValue("Thumbnails:Renderer", "stl-thumb");
        var sizeRaw = _config.GetValue("Thumbnails:Size", "256")!;
        // stl-thumb expects a single integer for square thumbnails, not WxH format
        var size = sizeRaw.Contains('x') ? sizeRaw.Split('x')[0] : sizeRaw;

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var psi = new ProcessStartInfo
        {
            FileName = renderer!,
            Arguments = $"\"{stlPath}\" \"{outputPath}\" --size {size}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                _logger.LogWarning("Failed to start thumbnail renderer: {Renderer}", renderer);
                return;
            }

            // Use a timeout to avoid hanging on a single file
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            await process.WaitForExitAsync(timeoutCts.Token);

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync(ct);
                _logger.LogWarning("Thumbnail generation failed for {Path}: {Error}", stlPath, stderr);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Thumbnail generation timed out for {Path}", stlPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thumbnail generation failed for {Path}", stlPath);
        }
    }

    public async Task ProcessPendingAsync(CancellationToken ct = default)
    {
        if (!_config.GetValue("Thumbnails:Enabled", true))
            return;

        if (!IsRendererAvailable())
            return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var basePaths = _config.GetSection("Storage:BasePaths").Get<string[]>() ?? ["/mnt/3dprinting"];
        var thumbDir = _config.GetValue("Storage:ThumbnailDir", ".thumbnails")!;
        var format = _config.GetValue("Thumbnails:Format", "webp")!;

        // Process priority queue first (user-requested thumbnails)
        while (_priorityQueue.TryDequeue(out var variantId))
        {
            ct.ThrowIfCancellationRequested();
            var variant = await db.Variants
                .Include(v => v.Model)
                .FirstOrDefaultAsync(v => v.Id == variantId, ct);

            if (variant == null) continue;
            await GenerateForVariantAsync(variant, basePaths[0], thumbDir, format, db, ct);
        }

        // Find variants without thumbnails (STL/OBJ only)
        var pendingVariants = await db.Variants
            .Where(v => v.ThumbnailPath == null &&
                       (v.FileType == Core.Enums.FileType.Stl || v.FileType == Core.Enums.FileType.Obj))
            .Include(v => v.Model)
            .Take(500) // Process in batches (5 min interval × 500 = ~6,000/hr)
            .ToListAsync(ct);

        foreach (var variant in pendingVariants)
        {
            ct.ThrowIfCancellationRequested();
            await GenerateForVariantAsync(variant, basePaths[0], thumbDir, format, db, ct);
        }
    }

    private async Task GenerateForVariantAsync(
        Core.Models.Variant variant, string basePath, string thumbDir, string format,
        ForgeDbContext db, CancellationToken ct)
    {
        var stlPath = Path.Combine(variant.Model.BasePath, variant.FilePath);
        if (!File.Exists(stlPath))
            return;

        var thumbFileName = $"{variant.Id}.{format}";
        var thumbPath = Path.Combine(basePath, ".forgekeeper", thumbDir, thumbFileName);

        await GenerateThumbnailAsync(stlPath, thumbPath, ct);

        if (File.Exists(thumbPath))
        {
            variant.ThumbnailPath = thumbPath;

            // Also set as model thumbnail if model doesn't have one
            if (variant.Model.ThumbnailPath == null)
                variant.Model.ThumbnailPath = thumbPath;

            await db.SaveChangesAsync(ct);
        }
        else
        {
            // Thumbnail wasn't created — generation failed or timed out. Record for visibility.
            using var scope = _scopeFactory.CreateScope();
            var fileIssueService = scope.ServiceProvider.GetRequiredService<FileIssueService>();
            await fileIssueService.ReportIssueAsync(
                stlPath, "thumbnail_fail",
                "Thumbnail generation failed or timed out",
                variant.Id, variant.ModelId, ct);
        }
    }
}
