using System.IO.Compression;
using System.Net;

namespace Forgekeeper.Scraper.Mmf;

public enum DownloadStatus { Success, NotFound, AuthExpired, CloudflareChallenge, Failed }

public sealed record DownloadOutcome(DownloadStatus Status, long Bytes = 0, string? Error = null, int Attempts = 0)
{
    public bool Success => Status == DownloadStatus.Success;
}

public sealed class MmfDownloadOptions
{
    public int MaxAttempts { get; init; } = 4;
    /// <summary>Backoff base; attempt n waits base * 4^(n-1) (2s, 8s, 32s by default).</summary>
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Upper bound for an honoured Retry-After so a hostile header can't stall us for hours.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromMinutes(10);
    public int MaxRedirects { get; init; } = 8;
    /// <summary>Test seam for waits.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
}

/// <summary>
/// Phase 2 session download: follows the /download/{id}?archive_id=… redirect chain manually
/// (MMF credentials only go to *.myminifactory.com, never to the file-storage host), streams
/// to a temp file in the destination folder, verifies size + zip integrity, then moves it into
/// place atomically. Partial files are always removed on failure.
/// </summary>
public sealed class MmfDownloader(HttpMessageHandler handler, MmfDownloadOptions? options = null)
{
    private readonly MmfDownloadOptions _opt = options ?? new MmfDownloadOptions();
    private readonly HttpClient _client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(60) };

    public async Task<DownloadOutcome> DownloadAsync(string url, string finalPath, MmfCredentials creds,
        long expectedSize = 0, CancellationToken ct = default)
    {
        string? lastError = null;
        for (int attempt = 1; attempt <= _opt.MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan? wait = null;
            try
            {
                using var resp = await SendFollowingRedirectsAsync(new Uri(url), creds, ct);
                var ctype = resp.Content.Headers.ContentType?.MediaType;
                if (resp.IsSuccessStatusCode && !(ctype?.Contains("html", StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    var bytes = await StreamToFinalAsync(resp, finalPath, expectedSize, ct);
                    return new DownloadOutcome(DownloadStatus.Success, bytes, Attempts: attempt);
                }

                var body = await SafePeekAsync(resp, ct);
                var kind = MmfSession.Classify(resp.StatusCode, ctype, body);
                switch (kind)
                {
                    case MmfResponseKind.NotFound:
                        return new DownloadOutcome(DownloadStatus.NotFound, Error: "404", Attempts: attempt);
                    case MmfResponseKind.AuthExpired:
                        return new DownloadOutcome(DownloadStatus.AuthExpired, Error: $"{(int)resp.StatusCode} (auth)", Attempts: attempt);
                    case MmfResponseKind.CloudflareChallenge:
                        return new DownloadOutcome(DownloadStatus.CloudflareChallenge, Error: "Cloudflare challenge (HTML)", Attempts: attempt);
                    case MmfResponseKind.RateLimited:
                        wait = RetryAfter(resp) ?? Backoff(attempt);
                        lastError = "429 Too Many Requests";
                        break;
                    case MmfResponseKind.ServerError:
                        lastError = $"HTTP {(int)resp.StatusCode}";
                        break;
                    default:
                        return new DownloadOutcome(DownloadStatus.Failed, Error: $"HTTP {(int)resp.StatusCode}", Attempts: attempt);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (InvalidDataException ex) { lastError = ex.Message; }          // size/zip verification
            catch (HttpRequestException ex) { lastError = ex.Message; }
            catch (IOException ex) { lastError = ex.Message; }
            catch (TaskCanceledException) { lastError = "timeout"; }

            if (attempt < _opt.MaxAttempts)
                await _opt.Delay(wait ?? Backoff(attempt), ct);
        }
        return new DownloadOutcome(DownloadStatus.Failed, Error: lastError ?? "failed", Attempts: _opt.MaxAttempts);
    }

    internal TimeSpan Backoff(int attempt) => TimeSpan.FromTicks(_opt.BaseBackoff.Ticks * (long)Math.Pow(4, attempt - 1));

    private TimeSpan? RetryAfter(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        TimeSpan? t = ra?.Delta ?? (ra?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
        if (t is null) return null;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t > _opt.MaxRetryAfter ? _opt.MaxRetryAfter : t;
    }

    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(Uri uri, MmfCredentials creds, CancellationToken ct)
    {
        for (int hop = 0; ; hop++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, uri);
            creds.Apply(req); // cookies/bearer only attached for MMF hosts
            var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)resp.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308 && resp.Headers.Location is { } loc)
            {
                resp.Dispose();
                if (hop >= _opt.MaxRedirects) throw new HttpRequestException("Too many redirects");
                uri = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                continue;
            }
            return resp;
        }
    }

    private static async Task<string?> SafePeekAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var s = await resp.Content.ReadAsStringAsync(ct);
            return s.Length > 2048 ? s[..2048] : s;
        }
        catch { return null; }
    }

    private static async Task<long> StreamToFinalAsync(HttpResponseMessage resp, string finalPath, long expectedSize, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.part");
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                await resp.Content.CopyToAsync(fs, ct);

            var len = new FileInfo(tmp).Length;
            var header = resp.Content.Headers.ContentLength;
            if (len == 0) throw new InvalidDataException("Empty download");
            if (header is > 0 && header != len) throw new InvalidDataException($"Truncated download: {len} of {header} bytes");
            if (expectedSize > 0 && expectedSize != len) throw new InvalidDataException($"Size mismatch: got {len}, expected {expectedSize}");
            if (finalPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) VerifyZip(tmp);

            File.Move(tmp, finalPath, overwrite: true); // same directory => atomic rename
            return len;
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    internal static void VerifyZip(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var e in zip.Entries) _ = e.Length; // forces central-directory read
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"Corrupt zip: {ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"Corrupt zip: {ex.Message}");
        }
    }
}
