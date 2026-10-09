namespace Forgekeeper.Scraper.Mmf;

/// <summary>Pacing settings for the session download path (DOWNLOAD_DELAY_SECONDS, API_CALL_DELAY_MS).</summary>
internal static class MmfPacing
{
    internal const double DefaultItemDelaySeconds = 20;
    internal const int DefaultApiDelayMs = 1500;
    internal const double Jitter = 0.25;

    /// <summary>Test seam for the jitter source.</summary>
    internal static Func<double>? RandomOverride { get; set; }
    private static double NextRandom() => RandomOverride?.Invoke() ?? Random.Shared.NextDouble();

    internal static double ItemDelaySeconds(IReadOnlyDictionary<string, string> config) =>
        config.TryGetValue("DOWNLOAD_DELAY_SECONDS", out var v) && double.TryParse(v, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s, 0, 3600) : DefaultItemDelaySeconds;

    internal static int ApiDelayMs(IReadOnlyDictionary<string, string> config) =>
        config.TryGetValue("API_CALL_DELAY_MS", out var v) && int.TryParse(v, out var ms) ? Math.Clamp(ms, 0, 60_000) : DefaultApiDelayMs;

    /// <summary>base ±25% (uniform).</summary>
    internal static TimeSpan WithJitter(TimeSpan baseDelay)
    {
        if (baseDelay <= TimeSpan.Zero) return TimeSpan.Zero;
        var factor = 1 - Jitter + 2 * Jitter * NextRandom();
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * factor);
    }

    internal static TimeSpan ItemDelay(IReadOnlyDictionary<string, string> config) =>
        WithJitter(TimeSpan.FromSeconds(ItemDelaySeconds(config)));

    internal static TimeSpan ApiDelay(IReadOnlyDictionary<string, string> config) =>
        WithJitter(TimeSpan.FromMilliseconds(ApiDelayMs(config)));
}
