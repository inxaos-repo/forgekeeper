using System.Security.Cryptography;
using Forgekeeper.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Forgekeeper.Api.BackgroundServices;

/// <summary>
/// Background service that progressively computes SHA-256 hashes for all file variants.
/// Hashes are stored on Variant.FileHash as "sha256:&lt;lowercase-hex&gt;" and written back
/// to metadata.json fileHashes. Runs in small batches with configurable pauses to avoid
/// saturating NFS I/O during active sync or scan operations.
///
/// Config keys:
///   Hashing:Enabled          — master toggle (default true)
///   Hashing:BatchSize         — variants per batch (default 50)
///   Hashing:IntervalSeconds   — pause between batches (default 5)
///   Hashing:IdleIntervalHours — sleep when all files hashed (default 1)
/// </summary>
public class HashWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<HashWorker> _logger;

    // Track progress for metrics / status endpoint
    public long HashedCount { get; private set; }
    public long TotalCount { get; private set; }
    public long ErrorCount { get; private set; }
    public bool IsRunning { get; private set; }

    public HashWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<HashWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("Hashing:Enabled", true))
        {
            _logger.LogInformation("[Hash] Worker disabled (set Hashing:Enabled=true to enable)");
            return;
        }

        var batchSize = _config.GetValue("Hashing:BatchSize", 50);
        var intervalSeconds = _config.GetValue("Hashing:IntervalSeconds", 5);
        var idleHours = _config.GetValue("Hashing:IdleIntervalHours", 1);

        // Let other workers start first (scanner, thumbnails, plugins)
        _logger.LogInformation("[Hash] Worker starting — waiting 3 minutes for other services");
        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);

        _logger.LogInformation("[Hash] Worker active — batch={Batch}, interval={Interval}s", batchSize, intervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                IsRunning = true;
                var processed = await ProcessBatchAsync(batchSize, stoppingToken);

                if (processed == 0)
                {
                    // All files hashed — sleep longer
                    IsRunning = false;
                    _logger.LogDebug("[Hash] All files hashed — sleeping {Hours}h", idleHours);
                    await Task.Delay(TimeSpan.FromHours(idleHours), stoppingToken);
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Hash] Unexpected error in hash worker loop");
                IsRunning = false;
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        IsRunning = false;
        _logger.LogInformation("[Hash] Worker stopped — hashed {Count} files total", HashedCount);
    }

    private async Task<int> ProcessBatchAsync(int batchSize, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        // Get total counts for progress tracking
        TotalCount = await db.Variants.LongCountAsync(ct);

        // Grab a batch of unhashed variants
        var batch = await db.Variants
            .Where(v => v.FileHash == null)
            .OrderBy(v => v.Id)
            .Take(batchSize)
            .ToListAsync(ct);

        if (batch.Count == 0)
            return 0;

        var batchHashed = 0;

        foreach (var variant in batch)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var hash = await ComputeSha256Async(variant.FilePath, ct);
                if (hash != null)
                {
                    variant.FileHash = $"sha256:{hash}";
                    batchHashed++;
                    HashedCount++;
                }
                else
                {
                    // File doesn't exist on disk — mark with sentinel so we don't retry forever
                    variant.FileHash = "sha256:missing";
                    ErrorCount++;
                    _logger.LogDebug("[Hash] File missing: {Path}", variant.FilePath);
                }
            }
            catch (Exception ex)
            {
                ErrorCount++;
                _logger.LogWarning(ex, "[Hash] Error hashing {Path}", variant.FilePath);
                // Skip this file, try again later (leave FileHash null)
            }
        }

        // Batch save
        await db.SaveChangesAsync(ct);

        if (batchHashed > 0)
        {
            _logger.LogDebug("[Hash] Batch complete — hashed {Count} files", batchHashed);

            // Write hashes back to metadata.json for each affected model
            await WritebackHashesAsync(db, batch, ct);
        }

        return batch.Count;
    }

    /// <summary>
    /// Update metadata.json fileHashes for each model affected by this batch.
    /// Groups variants by model to avoid multiple writes to the same file.
    /// </summary>
    private async Task WritebackHashesAsync(ForgeDbContext db, List<Core.Models.Variant> variants, CancellationToken ct)
    {
        // Get distinct model IDs from this batch
        var modelIds = variants.Select(v => v.ModelId).Distinct().ToList();

        foreach (var modelId in modelIds)
        {
            try
            {
                var model = await db.Models
                    .Include(m => m.Variants)
                    .FirstOrDefaultAsync(m => m.Id == modelId, ct);

                if (model == null || string.IsNullOrEmpty(model.BasePath))
                    continue;

                var metadataPath = Path.Combine(model.BasePath, "metadata.json");
                var fileHashes = model.Variants
                    .Where(v => v.FileHash != null && !v.FileHash.EndsWith(":missing"))
                    .ToDictionary(
                        v => Path.GetRelativePath(model.BasePath, v.FilePath),
                        v => v.FileHash!);

                if (fileHashes.Count == 0)
                    continue;

                // Read existing metadata.json, add/update fileHashes field
                Dictionary<string, object?>? existing = null;
                if (File.Exists(metadataPath))
                {
                    var json = await File.ReadAllTextAsync(metadataPath, ct);
                    existing = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }

                existing ??= new Dictionary<string, object?>();
                existing["fileHashes"] = fileHashes;

                var output = System.Text.Json.JsonSerializer.Serialize(existing,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true,
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    });

                await File.WriteAllTextAsync(metadataPath, output, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Hash] Failed to write hashes to metadata.json for model {ModelId}", modelId);
            }
        }
    }

    /// <summary>
    /// Compute SHA-256 hash of a file using async I/O with an 80KB buffer for NFS efficiency.
    /// Returns lowercase hex string, or null if file doesn't exist.
    /// </summary>
    private static async Task<string?> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
            return null;

        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
