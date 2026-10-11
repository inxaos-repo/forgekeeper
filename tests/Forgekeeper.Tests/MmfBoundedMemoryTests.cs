using System.IO.Compression;
using System.Net;
using Forgekeeper.Scraper.Mmf;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Forgekeeper.Tests;

/// <summary>
/// #74 regression: downloading and extracting a large archive must use bounded memory
/// (allocations independent of archive size) and downloads must wait on the extraction backlog.
/// </summary>
public class MmfBoundedMemoryTests(ITestOutputHelper output) : IDisposable
{
    private const long BigBytes = 256L << 20; // 256 MiB entry: real-world big MMF zips are 1–4 GiB
    private const long AllocBudget = 48L << 20; // whole download+extract must allocate far less than the file
    private readonly string _dir = Directory.CreateTempSubdirectory("fk-oom74-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FileHandler(string path) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(fs) };
            resp.Content.Headers.ContentType = new("application/zip");
            resp.Content.Headers.ContentLength = fs.Length;
            return Task.FromResult(resp);
        }
    }

    /// <summary>Stored (uncompressed) zip with one big pseudo-random entry, written in chunks.</summary>
    private string MakeBigZip()
    {
        var path = Path.Combine(_dir, "source.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var s = zip.CreateEntry("big/model.stl", CompressionLevel.NoCompression).Open();
        var rng = new Random(74);
        var chunk = new byte[1 << 20];
        for (long w = 0; w < BigBytes; w += chunk.Length) { rng.NextBytes(chunk); s.Write(chunk); }
        return path;
    }

    [Fact]
    public async Task LargeArchive_DownloadAndExtract_AllocationsStayBounded()
    {
        var src = MakeBigZip();
        var dest = Path.Combine(_dir, "dl", "model.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var dl = new MmfDownloader(new FileHandler(src), new MmfDownloadOptions { Delay = (_, _) => Task.CompletedTask });

        GC.Collect(); GC.WaitForPendingFinalizers();
        var before = GC.GetTotalAllocatedBytes(precise: true);

        var outcome = await dl.DownloadAsync("https://www.myminifactory.com/download/1", dest, new MmfCredentials("a=b", null, "UA"));
        Assert.True(outcome.Success, outcome.Error);
        var afterDl = GC.GetTotalAllocatedBytes(precise: true);

        var res = await MmfArchiveExtractor.ExtractAsync(dest, Path.Combine(_dir, "out"), NullLogger.Instance, deleteOnSuccess: true);
        Assert.True(res.Success, string.Join("; ", res.Errors));
        var afterEx = GC.GetTotalAllocatedBytes(precise: true);

        output.WriteLine($"download allocated {(afterDl - before) >> 20} MiB, extract {(afterEx - afterDl) >> 20} MiB for {BigBytes >> 20} MiB archive");
        Assert.Equal(BigBytes, new FileInfo(Path.Combine(_dir, "out", "big", "model.stl")).Length);
        Assert.True(afterDl - before < AllocBudget, $"download allocated {(afterDl - before) >> 20} MiB");
        Assert.True(afterEx - afterDl < AllocBudget, $"extract allocated {(afterEx - afterDl) >> 20} MiB");
    }

    [Fact]
    public async Task BoundedCopy_LargeStream_ConstantMemoryAndExactBytes()
    {
        var src = new ZeroStream(BigBytes);
        var path = Path.Combine(_dir, "copy.bin");
        var before = GC.GetTotalAllocatedBytes(true);
        long n;
        await using (var fs = MmfBoundedIo.CreateSequential(path)) n = await MmfBoundedIo.CopyAsync(src, fs, default);
        var alloc = GC.GetTotalAllocatedBytes(true) - before;
        Assert.Equal(BigBytes, n);
        Assert.Equal(BigBytes, new FileInfo(path).Length);
        Assert.True(alloc < 8L << 20, $"copy allocated {alloc >> 20} MiB");
    }

    [Fact]
    public async Task BoundedCopy_EnforcesMaxBytes()
    {
        await using var fs = MmfBoundedIo.CreateSequential(Path.Combine(_dir, "cap.bin"));
        await Assert.ThrowsAsync<InvalidDataException>(() => MmfBoundedIo.CopyAsync(new ZeroStream(10 << 20), fs, default, maxBytes: 1 << 20));
    }

    [Fact]
    public async Task Peek_ReadsOnlyPrefixOfHugeBody()
    {
        var content = new StreamContent(new ZeroStream(1L << 34)); // 16 GiB, would OOM if buffered
        var s = await MmfBoundedIo.PeekAsync(content, default);
        Assert.Equal(2048, s!.Length);
    }

    [Fact]
    public async Task PeekAndRewind_BodyStillFullyReadable()
    {
        var data = new byte[10_000]; new Random(1).NextBytes(data);
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(data)) };
        resp.Content.Headers.ContentLength = data.Length;
        await MmfBoundedIo.PeekAndRewindAsync(resp, default);
        Assert.Equal(data.Length, resp.Content.Headers.ContentLength);
        Assert.Equal(data, await resp.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task UnzipBacklog_BlocksDownloadsUntilDrained()
    {
        var plugin = new MmfScraperPlugin();
        var gate = new TaskCompletionSource();
        // simulate a running unzip worker holding two queued archives
        typeof(MmfScraperPlugin).GetField("_unzipTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(plugin, gate.Task);
        plugin.EnqueueUnzip(("a.zip", "a", "a.zip"));
        plugin.EnqueueUnzip(("b.zip", "b", "b.zip"));

        var wait = plugin.WaitForUnzipBacklogAsync(2, NullLogger.Instance, default, TimeSpan.FromMilliseconds(10));
        await Task.Delay(100);
        Assert.False(wait.IsCompleted);

        DrainOne(plugin);
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, plugin.UnzipPending);

        // worker gone → never deadlock
        gate.SetResult();
        plugin.EnqueueUnzip(("c.zip", "c", "c.zip"));
        await plugin.WaitForUnzipBacklogAsync(2, NullLogger.Instance, default).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData("0", 0)]
    [InlineData("5", 5)]
    [InlineData("junk", 2)]
    public void MaxUnzipBacklog_ParsesConfig(string? v, int expected)
    {
        var cfg = v == null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["MAX_UNZIP_BACKLOG"] = v };
        Assert.Equal(expected, MmfScraperPlugin.MaxUnzipBacklog(cfg));
    }

    private static void DrainOne(MmfScraperPlugin p)
    {
        var f = typeof(MmfScraperPlugin).GetField("_unzipPending", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        f.SetValue(p, (int)f.GetValue(p)! - 1);
    }

    private sealed class ZeroStream(long length) : Stream
    {
        private long _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _pos);
            Array.Clear(buffer, offset, n);
            _pos += n;
            return n;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => length; public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
