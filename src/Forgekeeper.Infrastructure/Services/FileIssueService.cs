using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// Tracks recurring file-level errors for surfacing in the UI and Prometheus metrics.
/// Upserts on (FilePath, IssueType) — one record per unique failure type per file.
/// Registered as Scoped; inject IServiceScopeFactory when calling from singletons.
/// </summary>
public class FileIssueService
{
    private readonly ForgeDbContext _db;
    private readonly ILogger<FileIssueService> _logger;

    public FileIssueService(ForgeDbContext db, ILogger<FileIssueService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Report a file issue. Upserts on (FilePath, IssueType):
    /// if a record already exists, updates LastSeen, increments Attempts, and re-opens if dismissed.
    /// </summary>
    public async Task ReportIssueAsync(
        string filePath,
        string issueType,
        string? errorMessage,
        Guid? variantId = null,
        Guid? modelId = null,
        CancellationToken ct = default)
    {
        try
        {
            var existing = await _db.FileIssues
                .FirstOrDefaultAsync(x => x.FilePath == filePath && x.IssueType == issueType, ct);

            if (existing is not null)
            {
                existing.LastSeen = DateTime.UtcNow;
                existing.Attempts++;
                if (errorMessage is not null)
                    existing.ErrorMessage = errorMessage;

                // Re-open if the issue was dismissed — it's happening again
                if (existing.Dismissed)
                {
                    existing.Dismissed = false;
                    existing.DismissedAt = null;
                    existing.DismissedBy = null;
                }
            }
            else
            {
                _db.FileIssues.Add(new FileIssue
                {
                    Id = Guid.NewGuid(),
                    FilePath = filePath,
                    IssueType = issueType,
                    ErrorMessage = errorMessage,
                    VariantId = variantId,
                    ModelId = modelId,
                    FirstSeen = DateTime.UtcNow,
                    LastSeen = DateTime.UtcNow,
                    Attempts = 1,
                });
            }

            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FileIssue] Failed to record issue {IssueType} for {FilePath}", issueType, filePath);
        }
    }

    /// <summary>
    /// Mark an issue as dismissed (will be re-opened if the problem recurs).
    /// </summary>
    public async Task DismissAsync(Guid issueId, string dismissedBy = "user", CancellationToken ct = default)
    {
        var issue = await _db.FileIssues.FindAsync([issueId], ct);
        if (issue is null)
            return;

        issue.Dismissed = true;
        issue.DismissedBy = dismissedBy;
        issue.DismissedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// List active (non-dismissed) issues, optionally filtered by type, with pagination.
    /// </summary>
    public async Task<List<FileIssue>> GetActiveIssuesAsync(
        string? issueType = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var query = _db.FileIssues
            .Where(x => !x.Dismissed)
            .AsQueryable();

        if (!string.IsNullOrEmpty(issueType))
            query = query.Where(x => x.IssueType == issueType);

        return await query
            .OrderByDescending(x => x.LastSeen)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns aggregate counts: total active, by type, and dismissed total.
    /// </summary>
    public async Task<object> GetSummaryAsync(CancellationToken ct = default)
    {
        var total = await _db.FileIssues.CountAsync(x => !x.Dismissed, ct);
        var dismissed = await _db.FileIssues.CountAsync(x => x.Dismissed, ct);

        var byType = await _db.FileIssues
            .Where(x => !x.Dismissed)
            .GroupBy(x => x.IssueType)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return new
        {
            total,
            byType = byType.ToDictionary(x => x.Type, x => x.Count),
            dismissed,
        };
    }

    /// <summary>
    /// Permanently remove all dismissed issues. Returns the count deleted.
    /// </summary>
    public async Task<int> PurgeDismissedAsync(CancellationToken ct = default)
    {
        var dismissed = await _db.FileIssues
            .Where(x => x.Dismissed)
            .ToListAsync(ct);

        _db.FileIssues.RemoveRange(dismissed);
        await _db.SaveChangesAsync(ct);
        return dismissed.Count;
    }
}
