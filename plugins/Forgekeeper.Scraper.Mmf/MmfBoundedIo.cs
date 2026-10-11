using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Forgekeeper.Scraper.Mmf;

/// <summary>
/// #74: bounded-memory streaming to disk. Large MMF archives (multi-GB) used to be copied with
/// default buffers and no write-back control: on the NFS library share dirty page cache piled up
/// faster than it was written back, and under a 2Gi cgroup limit reclaim could not keep up
/// (OOMKilled). This copier
///  * uses one pooled buffer (default 1 MiB): heap use is constant, independent of file size;
///  * every <see cref="SyncEveryBytes"/> flushes to stable storage (fsync), which blocks the
///    producer until the server has the data: natural backpressure, so dirty/writeback cache is
///    bounded to about one sync window;
///  * then drops the already-written range from the page cache (posix_fadvise DONTNEED, Linux
///    only, best effort) so clean cache does not crowd the cgroup either.
/// </summary>
public static class MmfBoundedIo
{
    public const int DefaultBufferSize = 1 << 20;           // 1 MiB
    public static long SyncEveryBytes { get; set; } = 32L << 20; // 32 MiB of dirty data at most

    /// <summary>Open a destination file for sequential writing (no extra FileStream buffer).</summary>
    public static FileStream CreateSequential(string path, FileMode mode = FileMode.Create) =>
        new(path, new FileStreamOptions
        {
            Mode = mode, Access = FileAccess.Write, Share = FileShare.None,
            BufferSize = 0, Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });

    /// <summary>Copy <paramref name="source"/> into <paramref name="dest"/> with bounded memory. Returns bytes copied.</summary>
    public static async Task<long> CopyAsync(Stream source, FileStream dest, CancellationToken ct,
        long maxBytes = long.MaxValue, int bufferSize = DefaultBufferSize)
    {
        var buf = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            long total = 0, sinceSync = 0, droppedUpTo = dest.Position;
            int n;
            while ((n = await source.ReadAsync(buf.AsMemory(0, bufferSize), ct)) > 0)
            {
                total += n;
                if (total > maxBytes) throw new InvalidDataException("actual size exceeds limit");
                await dest.WriteAsync(buf.AsMemory(0, n), ct);
                sinceSync += n;
                if (sinceSync >= SyncEveryBytes)
                {
                    droppedUpTo = SyncAndDrop(dest, droppedUpTo);
                    sinceSync = 0;
                }
            }
            SyncAndDrop(dest, droppedUpTo);
            return total;
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    /// <summary>Synchronous variant (zip entry streams are synchronous anyway).</summary>
    public static long Copy(Stream source, FileStream dest, CancellationToken ct,
        long maxBytes = long.MaxValue, int bufferSize = DefaultBufferSize)
    {
        var buf = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            long total = 0, sinceSync = 0, droppedUpTo = dest.Position;
            int n;
            while ((n = source.Read(buf, 0, bufferSize)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                total += n;
                if (total > maxBytes) throw new InvalidDataException("actual size exceeds limit");
                dest.Write(buf, 0, n);
                sinceSync += n;
                if (sinceSync >= SyncEveryBytes)
                {
                    droppedUpTo = SyncAndDrop(dest, droppedUpTo);
                    sinceSync = 0;
                }
            }
            if (total > 0) SyncAndDrop(dest, droppedUpTo);
            return total;
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    /// <summary>Stream an HTTP body into a file with bounded memory.</summary>
    public static async Task<long> CopyToFileAsync(HttpContent content, string path, CancellationToken ct)
    {
        await using var src = await content.ReadAsStreamAsync(ct);
        await using var fs = CreateSequential(path);
        return await CopyAsync(src, fs, ct);
    }

    /// <summary>Read at most <paramref name="maxBytes"/> of a body as text (never buffers the whole body).</summary>
    public static async Task<string?> PeekAsync(HttpContent content, CancellationToken ct, int maxBytes = 2048)
    {
        try
        {
            await using var s = await content.ReadAsStreamAsync(ct);
            var buf = new byte[maxBytes];
            var n = await s.ReadAtLeastAsync(buf, maxBytes, throwOnEndOfStream: false, ct);
            return System.Text.Encoding.UTF8.GetString(buf, 0, n);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Read at most <paramref name="maxBytes"/> of the body as text, then replace <c>resp.Content</c> with an
    /// equivalent streaming content (prefix + unread remainder) so the body can still be consumed once.
    /// </summary>
    public static async Task<string> PeekAndRewindAsync(HttpResponseMessage resp, CancellationToken ct, int maxBytes = 2048)
    {
        var old = resp.Content;
        var src = await old.ReadAsStreamAsync(ct);
        var buf = new byte[maxBytes];
        var n = await src.ReadAtLeastAsync(buf, maxBytes, throwOnEndOfStream: false, ct);
        var replacement = new StreamContent(new PrefixedStream(buf.AsMemory(0, n), src));
        foreach (var h in old.Headers) replacement.Headers.TryAddWithoutValidation(h.Key, h.Value);
        resp.Content = replacement;
        return System.Text.Encoding.UTF8.GetString(buf, 0, n);
    }

    private sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream rest) : Stream
    {
        private ReadOnlyMemory<byte> _prefix = prefix;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (_prefix.IsEmpty) return rest.Read(buffer);
            var n = Math.Min(buffer.Length, _prefix.Length);
            _prefix.Span[..n].CopyTo(buffer);
            _prefix = _prefix[n..];
            return n;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_prefix.IsEmpty) return rest.ReadAsync(buffer, ct);
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct) => ReadAsync(b.AsMemory(o, c), ct).AsTask();
        protected override void Dispose(bool disposing) { if (disposing) rest.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private static long SyncAndDrop(FileStream fs, long from)
    {
        fs.Flush(flushToDisk: true);
        var to = fs.Position;
        if (to > from) DropCache(fs.SafeFileHandle, from, to - from);
        return to;
    }

    private const int POSIX_FADV_DONTNEED = 4;
    private static bool _fadviseBroken;

    [DllImport("libc", EntryPoint = "posix_fadvise", SetLastError = false)]
    private static extern int posix_fadvise(SafeFileHandle fd, long offset, long len, int advice);

    internal static void DropCache(SafeFileHandle h, long offset, long len)
    {
        if (_fadviseBroken || !OperatingSystem.IsLinux()) return;
        try { _ = posix_fadvise(h, offset, len, POSIX_FADV_DONTNEED); }
        catch (Exception) { _fadviseBroken = true; } // EntryPointNotFound / DllNotFound: best effort only
    }
}
