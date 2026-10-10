using System.IO.Compression;
using System.Net;

namespace Forgekeeper.Scraper.Mmf;

public enum DownloadStatus { Success, NotFound, AuthExpired, CloudflareChallenge, Failed, NotAFile }

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
                var (resp0, finalUri) = await SendFollowingRedirectsAsync(new Uri(url), creds, ct);
                using var resp = resp0;
                var ctype = resp.Content.Headers.ContentType?.MediaType;
                // #48: a challenge is a challenge whatever the HTTP status (CF happily returns 200 HTML).
                if (MmfSession.HasChallengeHeader(resp))
                    return Challenge($"cf-mitigated header, HTTP {(int)resp.StatusCode}", ctype, null, attempt);
                var isHtml = ctype?.Contains("html", StringComparison.OrdinalIgnoreCase) ?? false;
                if (resp.IsSuccessStatusCode && !isHtml && MmfSession.IsExpectedDownloadContentType(ctype))
                {
                    var bytes = await StreamToFinalAsync(resp, finalPath, expectedSize, ct);
                    return new DownloadOutcome(DownloadStatus.Success, bytes, Attempts: attempt);
                }

                var body = await SafePeekAsync(resp, ct);
                if (MmfSession.LooksLikeChallengeBody(body))
                    return Challenge($"challenge body, HTTP {(int)resp.StatusCode}", ctype, body, attempt);
                if (resp.IsSuccessStatusCode)
                {
                    // #53: HTML without challenge markers/header is an MMF page (e.g. /download/{id} → 302 → /object/…):
                    // the item fails (reason recorded) and the run continues. Only real challenges pause.
                    if (MmfSession.LooksHtmlPublic(ctype, body))
                        return NotAFile(finalUri, (int)resp.StatusCode, ctype, body, attempt);
                    // Unexpected but non-HTML/non-challenge content type: let the size/magic checks decide.
                    var bytes = await StreamToFinalAsync(resp, finalPath, expectedSize, ct);
                    return new DownloadOutcome(DownloadStatus.Success, bytes, Attempts: attempt);
                }
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
                        return new DownloadOutcome(DownloadStatus.Failed, Error: $"HTTP {(int)resp.StatusCode} ({ctype ?? "no content-type"}, starts: {Snippet(body)})", Attempts: attempt);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (MmfChallengeContentException ex) { return Challenge(ex.Message, ex.ContentType, ex.Head, attempt); }
            catch (MmfPageContentException ex) { return new DownloadOutcome(DownloadStatus.NotAFile, Error: $"{ex.Message} (starts: {Snippet(ex.Head)})", Attempts: attempt); }
            catch (InvalidDataException ex) { lastError = ex.Message; }          // size/zip verification
            catch (HttpRequestException ex) { lastError = ex.Message; }
            catch (IOException ex) { lastError = ex.Message; }
            catch (TaskCanceledException) { lastError = "timeout"; }

            if (attempt < _opt.MaxAttempts)
                await _opt.Delay(wait ?? Backoff(attempt), ct);
        }
        return new DownloadOutcome(DownloadStatus.Failed, Error: lastError ?? "failed", Attempts: _opt.MaxAttempts);
    }

    /// <summary>#53: MMF answered with one of its own pages (object page, login page…) instead of a file.</summary>
    internal static DownloadOutcome NotAFile(Uri finalUri, int status, string? ctype, string? body, int attempt)
    {
        var where = IsObjectPage(finalUri) ? $"redirected to object page {finalUri.AbsolutePath}" : $"MMF page {finalUri.Host}{finalUri.AbsolutePath}";
        return new(DownloadStatus.NotAFile,
            Error: $"MMF page instead of file ({where}; HTTP {status}; content-type={ctype ?? "none"}; starts: {Snippet(body)})", Attempts: attempt);
    }

    internal static bool IsObjectPage(Uri u) => u.AbsolutePath.StartsWith("/object/", StringComparison.OrdinalIgnoreCase);

    private static DownloadOutcome Challenge(string why, string? ctype, string? body, int attempt) =>
        new(DownloadStatus.CloudflareChallenge,
            Error: $"Cloudflare challenge ({why}; content-type={ctype ?? "none"}; starts: {Snippet(body)})", Attempts: attempt);

    /// <summary>First bytes of a rejected body for the log (#48), single line, short — never the full page.</summary>
    internal static string Snippet(string? body)
    {
        if (string.IsNullOrEmpty(body)) return "(empty)";
        var s = new string(body.Take(80).Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return s.Length == 0 ? "(binary)" : s;
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

    private async Task<(HttpResponseMessage, Uri)> SendFollowingRedirectsAsync(Uri uri, MmfCredentials creds, CancellationToken ct)
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
            return (resp, uri);
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
            try { VerifyMagic(tmp, finalPath); }
            catch (InvalidDataException) { ThrowIfChallengeFile(tmp, resp); throw; }
            if (MmfSession.LooksHtmlPublic(null, ReadHead(tmp)) && ExpectedMagic(finalPath) is null)
                ThrowIfChallengeFile(tmp, resp, htmlIsChallenge: true);
            if (finalPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) VerifyZip(tmp);

            File.Move(tmp, finalPath, overwrite: true); // same directory => atomic rename
            return len;
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static string ReadHead(string path)
    {
        var buf = new byte[2048];
        int n;
        using (var fs = File.OpenRead(path)) n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
        return System.Text.Encoding.UTF8.GetString(buf, 0, n);
    }

    /// <summary>#48: a streamed "file" that is really a challenge page pauses the run instead of failing the item.</summary>
    private static void ThrowIfChallengeFile(string tmp, HttpResponseMessage resp, bool htmlIsChallenge = false)
    {
        var head = ReadHead(tmp);
        if (MmfSession.LooksLikeChallengeBody(head))
            throw new MmfChallengeContentException($"challenge page in file body, HTTP {(int)resp.StatusCode}",
                resp.Content.Headers.ContentType?.MediaType, head);
        // #53: plain HTML that is not a challenge is an MMF page — fail the item, don't pause.
        if (htmlIsChallenge && MmfSession.LooksHtmlPublic(null, head))
            throw new MmfPageContentException($"MMF page instead of file (HTML in file body, HTTP {(int)resp.StatusCode})", head);
    }

    /// <summary>
    /// Non-archive files (PDF rulebooks etc.) are saved as-is, never extracted. Check the leading
    /// bytes against the extension so an HTML login/challenge page can't be stored as "Rules.pdf".
    /// </summary>
    internal static void VerifyMagic(string path, string finalName)
    {
        var expected = ExpectedMagic(finalName);
        if (expected is null) return;
        var buf = new byte[expected.Length];
        int read;
        using (var fs = File.OpenRead(path)) read = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
        if (read < expected.Length || !buf.AsSpan(0, read).SequenceEqual(expected))
            throw new InvalidDataException($"Content does not match {Path.GetExtension(finalName)} signature");
    }

    internal static byte[]? ExpectedMagic(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "%PDF"u8.ToArray(),
        ".zip" => "PK"u8.ToArray(),
        ".7z" => new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C },
        ".rar" => "Rar!"u8.ToArray(),
        ".png" => new byte[] { 0x89, 0x50, 0x4E, 0x47 },
        ".jpg" or ".jpeg" => new byte[] { 0xFF, 0xD8, 0xFF },
        _ => null,
    };

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

internal sealed class MmfChallengeContentException(string message, string? contentType, string? head) : Exception(message)
{
    public string? ContentType { get; } = contentType;
    public string? Head { get; } = head;
}

internal sealed class MmfPageContentException(string message, string? head) : Exception(message)
{
    public string? Head { get; } = head;
}
