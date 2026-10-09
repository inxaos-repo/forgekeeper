using System.Text;
using System.Text.Json;
using Forgekeeper.PluginSdk;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Scraper.Mmf;

/// <summary>
/// Opt-in Cloudflare clearance refresh through FlareSolverr (FLARESOLVERR_URL).
/// Only cf_clearance / __cf_bm are taken from FlareSolverr and merged into the stored session;
/// the MMF login cookie (PHPSESSID etc.) is kept. FlareSolverr's User-Agent is adopted because
/// cf_clearance is bound to UA + egress IP. Never performs a credential login.
/// </summary>
internal static class MmfFlareSolverr
{
    internal static readonly string[] ClearanceCookies = ["cf_clearance", "__cf_bm"];

    /// <summary>Test seam for the FlareSolverr HTTP handler.</summary>
    internal static HttpMessageHandler? HandlerOverride { get; set; }

    internal static string? GetUrl(PluginContext context) =>
        context.Config.TryGetValue("FLARESOLVERR_URL", out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim().TrimEnd('/') : null;

    /// <summary>Replace/add the given cookies in a Cookie header, leaving all others untouched.</summary>
    internal static string MergeCookies(string? header, IReadOnlyDictionary<string, string> updates)
    {
        var parts = new List<KeyValuePair<string, string>>();
        foreach (var raw in (header ?? "").Split(';'))
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;
            var eq = p.IndexOf('=');
            var name = eq < 0 ? p : p[..eq];
            var val = eq < 0 ? "" : p[(eq + 1)..];
            if (updates.ContainsKey(name)) continue;
            parts.Add(new(name, val));
        }
        foreach (var u in updates) parts.Add(u);
        return string.Join("; ", parts.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    /// <summary>
    /// Ask FlareSolverr to solve the MMF home page. On success merges the clearance cookies into the
    /// stored session, adopts the UA and returns the refreshed credentials; otherwise null.
    /// </summary>
    internal static async Task<MmfCredentials?> RefreshAsync(PluginContext context, MmfCredentials creds, CancellationToken ct)
    {
        var url = GetUrl(context);
        if (url == null || !creds.HasSession) return null;
        try
        {
            using var client = HandlerOverride != null
                ? new HttpClient(HandlerOverride, disposeHandler: false)
                : new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(90);
            var payload = JsonSerializer.Serialize(new { cmd = "request.get", url = MmfScraperPlugin.SiteBase + "/", maxTimeout = 60000 });
            using var resp = await client.PostAsync(url + "/v1", new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
            if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) || !root.TryGetProperty("solution", out var sol))
            {
                context.Logger.LogWarning("[MMF][flaresolverr] Solve failed: status={Status} message={Message}", status,
                    root.TryGetProperty("message", out var m) ? m.GetString() : null);
                return null;
            }
            var ua = sol.TryGetProperty("userAgent", out var uaEl) ? uaEl.GetString() : null;
            var updates = new Dictionary<string, string>();
            if (sol.TryGetProperty("cookies", out var cookies) && cookies.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cookies.EnumerateArray())
                {
                    var name = c.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var value = c.TryGetProperty("value", out var v) ? v.GetString() : null;
                    if (name != null && value != null && ClearanceCookies.Contains(name)) updates[name] = value;
                }
            }
            if (!updates.ContainsKey("cf_clearance") || string.IsNullOrWhiteSpace(ua))
            {
                context.Logger.LogWarning("[MMF][flaresolverr] No cf_clearance/UA returned (cookies: {Names})", string.Join(",", updates.Keys));
                return null;
            }
            var merged = MergeCookies(creds.CookieHeader, updates);
            await context.TokenStore.SaveTokenAsync(MmfSession.CookieKey, merged, ct);
            await context.TokenStore.SaveTokenAsync(MmfSession.UserAgentKey, ua!, ct);
            context.Logger.LogInformation("[MMF][flaresolverr] Refreshed clearance cookies ({Names}); adopted FlareSolverr UA", string.Join(",", updates.Keys));
            return creds with { CookieHeader = merged, UserAgent = ua! };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            context.Logger.LogWarning("[MMF][flaresolverr] Request failed: {Error}", ex.Message);
            return null;
        }
    }
}
