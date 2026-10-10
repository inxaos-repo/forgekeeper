using System.IO.Compression;
using System.Net;
using System.Text;
using Forgekeeper.Infrastructure.Services;
using Forgekeeper.Scraper.Mmf;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>#53: /download/{id} → 302 → object page is an item failure, not a Cloudflare pause; failed ledger.</summary>
public class MmfIssue53Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fk53-" + Guid.NewGuid().ToString("N"));
    public MmfIssue53Tests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        { Seen.Add(r.RequestUri!); return Task.FromResult(f(r)); }
    }

    private static HttpResponseMessage Html(HttpStatusCode c, string body) =>
        new(c) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Redirect(string to)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(to);
        return r;
    }

    private static byte[] Zip()
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        using (var w = new StreamWriter(z.CreateEntry("p.stl").Open())) w.Write("solid x\nendsolid x\n");
        return ms.ToArray();
    }

    private async Task<(DownloadOutcome, Handler)> Dl(Func<HttpRequestMessage, HttpResponseMessage> f, string url = "https://www.myminifactory.com/download/802018")
    {
        var h = new Handler(f);
        var o = await new MmfDownloader(h, new MmfDownloadOptions { Delay = (_, _) => Task.CompletedTask })
            .DownloadAsync(url, Path.Combine(_dir, "a.zip"), new MmfCredentials("a=b", null, "UA"));
        return (o, h);
    }

    private const string ObjectPage = "<!DOCTYPE html><html><head><title>Bought Thing - MyMiniFactory</title></head><body>object</body></html>";

    [Fact]
    public async Task RedirectToObjectPage_IsNotAFile_WithReason_NoRetries()
    {
        var (o, h) = await Dl(r => r.RequestUri!.AbsolutePath == "/download/802018"
            ? Redirect("https://www.myminifactory.com/object/3d-print-bought-thing-802018")
            : Html(HttpStatusCode.OK, ObjectPage));
        Assert.Equal(DownloadStatus.NotAFile, o.Status);
        Assert.Contains("redirected to object page /object/3d-print-bought-thing-802018", o.Error);
        Assert.False(MmfScraperPlugin.IsChallengeError(o.Error));
        Assert.Equal(2, h.Seen.Count); // no retry loop
        Assert.False(File.Exists(Path.Combine(_dir, "a.zip")));
    }

    [Fact]
    public async Task RedirectToChallengePage_StillPauses()
    {
        var (o, _) = await Dl(r => r.RequestUri!.AbsolutePath == "/download/802018"
            ? Redirect("https://www.myminifactory.com/object/x-802018")
            : Html(HttpStatusCode.OK, "<html><head><title>Just a moment...</title></head><body>/cdn-cgi/challenge-platform/h/b</body></html>"));
        Assert.Equal(DownloadStatus.CloudflareChallenge, o.Status);
    }

    [Fact]
    public async Task CfMitigatedHeader_OnHtml_StillPauses()
    {
        var (o, _) = await Dl(_ => { var r = Html(HttpStatusCode.OK, ObjectPage); r.Headers.TryAddWithoutValidation("cf-mitigated", "challenge"); return r; });
        Assert.Equal(DownloadStatus.CloudflareChallenge, o.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task CloudflareHtml403or503_StillPauses(HttpStatusCode code)
    {
        var (o, _) = await Dl(_ => { var r = Html(code, "<html><body>Attention Required! | Cloudflare</body></html>"); r.Headers.TryAddWithoutValidation("Server", "cloudflare"); return r; });
        Assert.Equal(DownloadStatus.CloudflareChallenge, o.Status);
    }

    [Fact]
    public async Task PerFileArchiveIdUrl_PartialContentZip_Succeeds()
    {
        var bytes = Zip();
        var (o, _) = await Dl(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes) };
            r.Content.Headers.ContentType = new("application/zip");
            return r;
        }, "https://www.myminifactory.com/download/802018?archive_id=1");
        Assert.Equal(DownloadStatus.Success, o.Status);
        Assert.True(File.Exists(Path.Combine(_dir, "a.zip")));
    }

    [Fact]
    public void Ledger_RecordsFailure_PersistsAndClearsOnSuccess()
    {
        var t = new DateTime(2026, 10, 10, 16, 0, 0, DateTimeKind.Utc);
        var l = SyncFailedLedger.Load(_dir, "mmf");
        Assert.Equal(0, l.Count);
        l.RecordFailure("802018", "Creator", "Bought", "MMF page instead of file", t);
        l.RecordFailure("802018", null, null, "again", t.AddHours(1));
        l.Save();
        Assert.Equal(Path.Combine(_dir, "failed-mmf.json"), l.Path);

        var l2 = SyncFailedLedger.Load(_dir, "mmf");
        Assert.True(l2.TryGet("802018", out var e));
        Assert.Equal(2, e!.Count);
        Assert.Equal("again", e.Reason);
        Assert.Equal("Bought", e.Title);
        Assert.Equal(t, e.FirstFailedUtc);
        Assert.True(l2.RecordSuccess("802018"));
        Assert.False(l2.TryGet("802018", out _));
    }

    [Fact]
    public void Ledger_CorruptFile_LoadsEmpty()
    {
        File.WriteAllText(SyncFailedLedger.PathFor(_dir, "mmf"), "{not json");
        Assert.Equal(0, SyncFailedLedger.Load(_dir, "mmf").Count);
    }
}
