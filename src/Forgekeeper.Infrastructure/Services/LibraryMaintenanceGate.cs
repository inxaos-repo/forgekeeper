namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// #73: process-wide exclusive gate for library maintenance jobs that rewrite model rows and metadata.json.
/// The scanner refuses to start while it is held; a job refuses to start while a scan is running.
/// Ordering: the scanner checks the gate inside its progress lock before flagging IsRunning, and a job takes the
/// gate first and then reads IsRunning (also under that lock), so the two can never overlap.
/// </summary>
public static class LibraryMaintenanceGate
{
    private static string? _holder;

    public static bool IsHeld => Volatile.Read(ref _holder) != null;
    public static string? Holder => Volatile.Read(ref _holder);

    public static bool TryEnter(string holder) => Interlocked.CompareExchange(ref _holder, holder, null) == null;

    public static void Exit(string holder) => Interlocked.CompareExchange(ref _holder, null, holder);
}
