using System.Diagnostics;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Infrastructure.Services;

public class ThumbnailService : IThumbnailService
{
    private readonly IDbContextFactory<ForgeDbContext> _dbFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ThumbnailService> _logger;

    public ThumbnailService(
        IDbContextFactory<ForgeDbContext> dbFactory,
        IConfiguration config,
        ILogger<ThumbnailService> logger)
    {
        _dbFactory = dbFactory;
        _config = config;
        _logger = logger;
    }

    public async Task GenerateThumbnailAsync(string stlPath, string outputPath, CancellationToken ct = default)
    {
        var renderer = _config.GetValue("Thumbnails:Renderer", "stl-thumb");
        var size = _config.GetValue("Thumbnails:Size", "256x256");

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

            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync(ct);
                _logger.LogWarning("Thumbnail generation failed for {Path}: {Error}", stlPath, stderr);
            }
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

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var basePaths = _config.GetSection("Storage:BasePaths").Get<string[]>() ?? ["/mnt/3dprinting"];
        var thumbDir = _config.GetValue("Storage:ThumbnailDir", ".thumbnails")!;
        var format = _config.GetValue("Thumbnails:Format", "webp")!;

        // Find variants without thumbnails (STL/OBJ only)
        var pendingVariants = await db.Variants
            .Where(v => v.ThumbnailPath == null &&
                       (v.FileType == Core.Enums.FileType.Stl || v.FileType == Core.Enums.FileType.Obj))
            .Include(v => v.Model)
            .Take(100) // Process in batches
            .ToListAsync(ct);

        foreach (var variant in pendingVariants)
        {
            ct.ThrowIfCancellationRequested();

            var stlPath = Path.Combine(variant.Model.BasePath, variant.FilePath);
            if (!File.Exists(stlPath))
                continue;

            var thumbFileName = $"{variant.Id}.{format}";
            var thumbPath = Path.Combine(basePaths[0], ".forgekeeper", thumbDir, thumbFileName);

            await GenerateThumbnailAsync(stlPath, thumbPath, ct);

            if (File.Exists(thumbPath))
            {
                variant.ThumbnailPath = thumbPath;

                // Also set as model thumbnail if model doesn't have one
                if (variant.Model.ThumbnailPath == null)
                    variant.Model.ThumbnailPath = thumbPath;

                await db.SaveChangesAsync(ct);
            }
        }
    }
}
