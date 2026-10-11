using Forgekeeper.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// #73: runs <see cref="MmfOrphanMergeService"/> as a background job so it is not cut off by the 60s ingress
/// timeout (the request token used to cancel it mid-way). One job at a time; refuses while a library scan or a
/// plugin sync is running, and holds <see cref="LibraryMaintenanceGate"/> so no scan starts while it runs.
/// </summary>
public class MmfOrphanMergeJob(
    IServiceScopeFactory scopes,
    IScannerService scanner,
    Func<bool> anySyncRunning,
    ILogger<MmfOrphanMergeJob> logger)
{
    public const string GateName = "mmf-orphan-merge";

    public record JobStatus(
        string State, Guid? JobId, bool Apply, bool IncludeLive, DateTime? StartedAt, DateTime? CompletedAt,
        string? Error, string? ReportPath, object? Summary);

    private readonly object _lock = new();
    private JobStatus _status = new("idle", null, false, false, null, null, null, null, null);

    public JobStatus Status { get { lock (_lock) return _status; } }

    /// <summary>Why a job can't start right now, or null if it can.</summary>
    public string? BlockReason()
    {
        if (Status.State == "running") return "an orphan merge job is already running";
        if (scanner.IsRunning) return "a library scan is running";
        if (anySyncRunning()) return "a plugin sync is running";
        if (LibraryMaintenanceGate.IsHeld) return $"library maintenance running: {LibraryMaintenanceGate.Holder}";
        return null;
    }

    /// <summary>Start the job. Returns (started, reason). The job is awaited by <paramref name="run"/> for tests.</summary>
    public (bool Started, string? Reason, Task? Run) TryStart(bool apply, bool includeLive, string reportDir, CancellationToken stopping)
    {
        lock (_lock)
        {
            if (_status.State == "running") return (false, "an orphan merge job is already running", null);
            if (anySyncRunning()) return (false, "a plugin sync is running", null);
            if (!LibraryMaintenanceGate.TryEnter(GateName))
                return (false, $"library maintenance running: {LibraryMaintenanceGate.Holder}", null);
            if (scanner.IsRunning)
            {
                LibraryMaintenanceGate.Exit(GateName);
                return (false, "a library scan is running", null);
            }
            _status = new JobStatus("running", Guid.NewGuid(), apply, includeLive, DateTime.UtcNow, null, null, null, null);
        }
        var run = Task.Run(() => RunAsync(apply, includeLive, reportDir, stopping), CancellationToken.None);
        return (true, null, run);
    }

    private async Task RunAsync(bool apply, bool includeLive, string reportDir, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<MmfOrphanMergeService>();
            string? path = null;
            if (apply)
            {
                // Backup first: snapshot every row we'd delete, before touching anything.
                var plan = await svc.MergeOrphansAsync(false, includeLive, ct);
                path = await TryWrite(reportDir, "mmf-orphan-merge-backup", plan, ct);
                if (path == null && plan.Pairs > 0)
                    throw new InvalidOperationException($"could not write backup report to {reportDir}; refusing to apply");
            }
            var report = await svc.MergeOrphansAsync(apply, includeLive, ct);
            var reportPath = await TryWrite(reportDir,
                apply ? "mmf-orphan-merge-applied" : "mmf-orphan-merge-dryrun", report, ct) ?? path;
            var summary = new
            {
                report.Applied, report.Orphans, report.LiveOrphans, report.Pairs, report.Ambiguous, report.Unmatched,
                report.Merged, Skipped = report.SkippedList.Count, backupPath = path,
                ambiguousSample = report.AmbiguousList.Take(50), skippedSample = report.SkippedList.Take(50),
                pairSample = report.PairList.Take(20),
            };
            lock (_lock) _status = _status with { State = "completed", CompletedAt = DateTime.UtcNow, ReportPath = reportPath, Summary = summary };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MMF orphan merge job failed");
            lock (_lock) _status = _status with
            {
                State = ex is OperationCanceledException ? "cancelled" : "failed",
                CompletedAt = DateTime.UtcNow, Error = ex.Message,
            };
        }
        finally
        {
            LibraryMaintenanceGate.Exit(GateName);
        }
    }

    private static async Task<string?> TryWrite(string dir, string name, object report, CancellationToken ct)
    {
        try { return await MmfOrphanMergeService.WriteReportAsync(dir, name, report, ct); }
        catch (Exception) { return null; } // report dir may be unavailable in dev/tests
    }
}
