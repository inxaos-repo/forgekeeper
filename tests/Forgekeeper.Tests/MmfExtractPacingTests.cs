using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Forgekeeper.Infrastructure.Services;
using Forgekeeper.PluginSdk;
using Forgekeeper.Scraper.Mmf;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>RAR/7z + nested extraction, pacing, FlareSolverr clearance refresh, skip list. No real MMF calls.</summary>
[Collection("MmfApiHandlerOverride")]
public class MmfExtractPacingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fk-xp-").FullName;
    private readonly List<TimeSpan> _delays = new();

    public MmfExtractPacingTests()
    {
        MmfScraperPlugin.DelayOverride = (t, _) => { lock (_delays) _delays.Add(t); return Task.CompletedTask; };
    }

    public void Dispose()
    {
        MmfScraperPlugin.ApiHandlerOverride = null;
        MmfScraperPlugin.DelayOverride = null;
        MmfFlareSolverr.HandlerOverride = null;
        MmfPacing.RandomOverride = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---------- helpers ----------

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, data) in entries)
            {
                using var s = z.CreateEntry(name).Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    private static byte[] Stl(string n = "x") => Encoding.ASCII.GetBytes($"solid {n}\nendsolid {n}\n");

    private string WriteFile(string name, byte[] data)
    {
        var p = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
        return p;
    }

    private static bool Has7z() => MmfArchiveExtractor.FindSevenZip() != null;

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            if (request.Content != null) Bodies.Add(await request.Content.ReadAsStringAsync(ct));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Html(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Bytes(byte[] b)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };
        r.Content.Headers.ContentType = new("application/zip");
        return r;
    }

    private PluginContext Ctx(ITokenStore tokens, Dictionary<string, string> config) => new()
    {
        SourceDirectory = _dir,
        ModelDirectory = Path.Combine(_dir, "creator", "model"),
        Config = config,
        HttpClient = new HttpClient(new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
        Logger = NullLogger.Instance,
        TokenStore = tokens,
        Progress = new Progress<ScrapeProgress>(),
    };

    private static async Task<InMemoryTokenStore> SessionTokens()
    {
        var t = new InMemoryTokenStore();
        await t.SaveTokenAsync(MmfSession.CookieKey, "PHPSESSID=keepme; cf_clearance=old");
        await t.SaveTokenAsync(MmfSession.StateKey, "ok");
        return t;
    }

    private static ScrapedModel Model() => new() { ExternalId = "200", Name = "Cool Dragon", CreatorName = "creator" };

    private const string ObjectJson =
        "{\"id\":200,\"name\":\"Cool Dragon\",\"archive_download_url\":\"https://www.myminifactory.com/download/200?archive_id=9\"}";

    // ---------- zip extraction ----------

    [Fact]
    public async Task Zip_Extracts_AndDeletesOnlyWhenAsked()
    {
        var a = WriteFile("a.zip", Zip(("m/part.stl", Stl())));
        var dest = Path.Combine(_dir, "out");
        var r = await MmfArchiveExtractor.ExtractAsync(a, dest, NullLogger.Instance, deleteOnSuccess: true);
        Assert.True(r.Success);
        Assert.True(File.Exists(Path.Combine(dest, "m", "part.stl")));
        Assert.False(File.Exists(a));
    }

    [Fact]
    public async Task Zip_Slip_IsRejected_AndArchiveKept()
    {
        var a = WriteFile("evil.zip", Zip(("../../escape.stl", Stl())));
        var dest = Path.Combine(_dir, "out");
        var r = await MmfArchiveExtractor.ExtractAsync(a, dest, NullLogger.Instance, deleteOnSuccess: true);
        Assert.False(r.Success);
        Assert.Contains("zip-slip", r.Errors[0]);
        Assert.True(File.Exists(a));
        Assert.False(File.Exists(Path.Combine(_dir, "..", "escape.stl")));
    }

    [Fact]
    public async Task Zip_Bomb_TotalSizeLimit_IsRejected()
    {
        var a = WriteFile("bomb.zip", Zip(("big.stl", new byte[50_000])));
        var r = await MmfArchiveExtractor.ExtractAsync(a, Path.Combine(_dir, "out"), NullLogger.Instance, true,
            new ArchiveLimits { MaxTotalBytes = 10_000 });
        Assert.False(r.Success);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public async Task Zip_Bomb_RatioLimit_IsRejected()
    {
        var a = WriteFile("ratio.zip", Zip(("zeros.stl", new byte[2_000_000])));
        var r = await MmfArchiveExtractor.ExtractAsync(a, Path.Combine(_dir, "out"), NullLogger.Instance, true,
            new ArchiveLimits { MaxRatio = 10, RatioCheckMinBytes = 1000 });
        Assert.False(r.Success);
        Assert.Contains("ratio", r.Errors[0]);
    }

    [Fact]
    public async Task Zip_TooManyEntries_IsRejected()
    {
        var a = WriteFile("many.zip", Zip(("a.stl", Stl()), ("b.stl", Stl()), ("c.stl", Stl())));
        var r = await MmfArchiveExtractor.ExtractAsync(a, Path.Combine(_dir, "out"), NullLogger.Instance, true,
            new ArchiveLimits { MaxEntries = 2 });
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Corrupt_Zip_KeepsArchive()
    {
        var a = WriteFile("bad.zip", Encoding.ASCII.GetBytes("not a zip"));
        var r = await MmfArchiveExtractor.ExtractAsync(a, Path.Combine(_dir, "out"), NullLogger.Instance, true);
        Assert.False(r.Success);
        Assert.True(File.Exists(a));
    }

    // ---------- nested ----------

    [Fact]
    public async Task Nested_Zip_ExtractsIntoFolderNamedAfterArchive_AndDeletesInner()
    {
        var inner = Zip(("base.stl", Stl("b")));
        var a = WriteFile("outer.zip", Zip(("Haito/Main Base_25mm.zip", inner), ("Haito/hero.stl", Stl())));
        var dest = Path.Combine(_dir, "out");
        var r = await MmfArchiveExtractor.ExtractAsync(a, dest, NullLogger.Instance, true);
        Assert.True(r.Success);
        Assert.Equal(2, r.Extracted);
        Assert.True(File.Exists(Path.Combine(dest, "Haito", "Main Base_25mm", "base.stl")));
        Assert.False(File.Exists(Path.Combine(dest, "Haito", "Main Base_25mm.zip")));
    }

    [Fact]
    public async Task Nested_Depth_IsCappedAtTwo()
    {
        var level3 = Zip(("deep.stl", Stl()));
        var level2 = Zip(("l3.zip", level3));
        var a = WriteFile("outer.zip", Zip(("l2.zip", level2)));
        var dest = Path.Combine(_dir, "out");
        var r = await MmfArchiveExtractor.ExtractAsync(a, dest, NullLogger.Instance, true);
        Assert.True(r.Success);
        Assert.True(File.Exists(Path.Combine(dest, "l2", "l3.zip")));     // third level left as-is
        Assert.False(Directory.Exists(Path.Combine(dest, "l2", "l3")));
    }

    [Fact]
    public async Task Nested_Failure_KeepsInnerArchive_OuterStillSucceeds()
    {
        var a = WriteFile("outer.zip", Zip(("broken.zip", Encoding.ASCII.GetBytes("garbage")), ("ok.stl", Stl())));
        var dest = Path.Combine(_dir, "out");
        var r = await MmfArchiveExtractor.ExtractAsync(a, dest, NullLogger.Instance, true);
        Assert.True(r.Success);
        Assert.Single(r.Errors);
        Assert.True(File.Exists(Path.Combine(dest, "broken.zip")));
    }

    [Fact]
    public void InnerDir_IsSiblingNamedAfterArchive()
    {
        Assert.Equal(Path.Combine("/x/y", "Main Base_25mm"), MmfArchiveExtractor.InnerExtractDir("/x/y/Main Base_25mm.zip"));
        Assert.True(MmfArchiveExtractor.IsExtractable("a.RAR"));
        Assert.True(MmfArchiveExtractor.IsExtractable("a.7z"));
        Assert.False(MmfArchiveExtractor.IsExtractable("a.stl"));
    }

    // ---------- 7z / rar (needs the 7z binary; present in the runtime image) ----------

    private static void Run7z(params string[] args)
    {
        var psi = new ProcessStartInfo(MmfArchiveExtractor.FindSevenZip()!) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public async Task SevenZip_Archive_Extracts_WithNestedZip()
    {
        if (!Has7z()) return; // SDK image without p7zip: covered in the runtime image
        var src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(Path.Combine(src, "minis"));
        File.WriteAllBytes(Path.Combine(src, "minis", "a.stl"), Stl());
        File.WriteAllBytes(Path.Combine(src, "minis", "bases.zip"), Zip(("b.stl", Stl())));
        var a = Path.Combine(_dir, "pack.7z");
        Run7z("a", "-bd", a, Path.Combine(src, "minis"));
        var dest = Path.Combine(_dir, "out");
        var r = await MmfArchiveExtractor.ExtractAsync(a, dest, NullLogger.Instance, true);
        Assert.True(r.Success, string.Join(";", r.Errors));
        Assert.True(File.Exists(Path.Combine(dest, "minis", "a.stl")));
        Assert.True(File.Exists(Path.Combine(dest, "minis", "bases", "b.stl")));
        Assert.False(File.Exists(a));
    }

    [Fact]
    public async Task SevenZip_Corrupt_KeepsArchive()
    {
        if (!Has7z()) return;
        var a = WriteFile("broken.rar", Encoding.ASCII.GetBytes("Rar!\x1a\x07\x01\x00garbage"));
        var r = await MmfArchiveExtractor.ExtractAsync(a, Path.Combine(_dir, "out"), NullLogger.Instance, true);
        Assert.False(r.Success);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public async Task Missing7z_FailsAndKeepsArchive()
    {
        MmfArchiveExtractor.SevenZipPathOverride = "/nonexistent/7z";
        try
        {
            var a = WriteFile("x.7z", new byte[] { 1, 2, 3 });
            var r = await MmfArchiveExtractor.ExtractAsync(a, Path.Combine(_dir, "out"), NullLogger.Instance, true);
            Assert.False(r.Success);
            Assert.True(File.Exists(a));
        }
        finally { MmfArchiveExtractor.SevenZipPathOverride = null; }
    }

    // ---------- pacing ----------

    [Theory]
    [InlineData(0.0, 15.0)]
    [InlineData(0.5, 20.0)]
    [InlineData(1.0, 25.0)]
    public void ItemDelay_Default20s_Jitter25Percent(double rnd, double expectedSeconds)
    {
        MmfPacing.RandomOverride = () => rnd;
        Assert.Equal(expectedSeconds, MmfPacing.ItemDelay(new Dictionary<string, string>()).TotalSeconds, 3);
    }

    [Fact]
    public void Pacing_Settings_Parse_AndZeroDisables()
    {
        var cfg = new Dictionary<string, string> { ["DOWNLOAD_DELAY_SECONDS"] = "0", ["API_CALL_DELAY_MS"] = "200" };
        Assert.Equal(TimeSpan.Zero, MmfPacing.ItemDelay(cfg));
        Assert.Equal(200, MmfPacing.ApiDelayMs(cfg));
        Assert.Equal(MmfPacing.DefaultApiDelayMs, MmfPacing.ApiDelayMs(new Dictionary<string, string>()));
    }

    [Fact]
    public void Pacing_And_FlareSolverr_AreDeclaredSettings()
    {
        var schema = new MmfScraperPlugin().ConfigSchema;
        Assert.Equal("20", schema.Single(f => f.Key == "DOWNLOAD_DELAY_SECONDS").DefaultValue);
        Assert.Equal("1500", schema.Single(f => f.Key == "API_CALL_DELAY_MS").DefaultValue);
        Assert.Equal("", schema.Single(f => f.Key == "FLARESOLVERR_URL").DefaultValue); // opt-in
    }

    [Fact]
    public async Task SessionDownload_WaitsApiDelay_AndItemDelayAfterwards()
    {
        MmfPacing.RandomOverride = () => 0.5;
        var tokens = await SessionTokens();
        MmfScraperPlugin.ApiHandlerOverride = new RouteHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/api/v2/objects/200")) return Json(HttpStatusCode.OK, ObjectJson);
            if (url.Contains("/download/200")) return Bytes(Zip(("p.stl", Stl())));
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var r = await new MmfScraperPlugin().ScrapeModelAsync(Ctx(tokens, new() { ["DELAY_MS"] = "0" }), Model());
        Assert.True(r.Success, r.Error);
        Assert.Contains(TimeSpan.FromMilliseconds(1500), _delays);
        Assert.Contains(TimeSpan.FromSeconds(20), _delays);
    }

    // ---------- FlareSolverr ----------

    [Fact]
    public void MergeCookies_ReplacesOnlyClearance_KeepsSession()
    {
        var merged = MmfFlareSolverr.MergeCookies("PHPSESSID=keep; cf_clearance=old; other=1",
            new Dictionary<string, string> { ["cf_clearance"] = "new", ["__cf_bm"] = "bm" });
        Assert.Equal("PHPSESSID=keep; other=1; cf_clearance=new; __cf_bm=bm", merged);
    }

    private const string FsOk =
        "{\"status\":\"ok\",\"message\":\"\",\"solution\":{\"status\":200,\"userAgent\":\"FS-UA/1\",\"cookies\":[" +
        "{\"name\":\"cf_clearance\",\"value\":\"fresh\"},{\"name\":\"__cf_bm\",\"value\":\"bm\"},{\"name\":\"PHPSESSID\",\"value\":\"attacker\"}]}}";

    [Fact]
    public async Task CloudflareChallenge_WithFlareSolverr_RefreshesCookies_AdoptsUa_RetriesOnce()
    {
        var tokens = await SessionTokens();
        var fs = new RouteHandler(_ => Json(HttpStatusCode.OK, FsOk));
        MmfFlareSolverr.HandlerOverride = fs;
        int objectCalls = 0;
        var api = new RouteHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/api/v2/objects/200"))
                return Interlocked.Increment(ref objectCalls) == 1
                    ? Html(HttpStatusCode.Forbidden, "<html>Just a moment...</html>")
                    : Json(HttpStatusCode.OK, ObjectJson);
            if (url.Contains("/download/200")) return Bytes(Zip(("p.stl", Stl())));
            return Json(HttpStatusCode.NotFound, "{}");
        });
        MmfScraperPlugin.ApiHandlerOverride = api;
        var cfg = new Dictionary<string, string> { ["DELAY_MS"] = "0", ["FLARESOLVERR_URL"] = "http://fs.invalid:8191/" };
        var r = await new MmfScraperPlugin().ScrapeModelAsync(Ctx(tokens, cfg), Model());

        Assert.True(r.Success, r.Error);
        Assert.Single(fs.Requests);
        Assert.Equal("http://fs.invalid:8191/v1", fs.Requests[0].RequestUri!.ToString());
        Assert.DoesNotContain("password", fs.Bodies[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("PHPSESSID=keepme; cf_clearance=fresh; __cf_bm=bm", await tokens.GetTokenAsync(MmfSession.CookieKey));
        Assert.Equal("FS-UA/1", await tokens.GetTokenAsync(MmfSession.UserAgentKey));
        var retry = api.Requests.Where(q => q.RequestUri!.ToString().Contains("/objects/200")).Last();
        Assert.Contains("FS-UA/1", retry.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task CloudflareChallenge_StillBlockedAfterRetry_Pauses()
    {
        var tokens = await SessionTokens();
        var fs = new RouteHandler(_ => Json(HttpStatusCode.OK, FsOk));
        MmfFlareSolverr.HandlerOverride = fs;
        MmfScraperPlugin.ApiHandlerOverride = new RouteHandler(_ => Html(HttpStatusCode.Forbidden, "<html>Just a moment...</html>"));
        var cfg = new Dictionary<string, string> { ["DELAY_MS"] = "0", ["FLARESOLVERR_URL"] = "http://fs.invalid:8191" };
        var r = await new MmfScraperPlugin().ScrapeModelAsync(Ctx(tokens, cfg), Model());
        Assert.True(r.AuthExpired);
        Assert.Single(fs.Requests); // exactly one retry
    }

    [Fact]
    public async Task CloudflareChallenge_WithoutFlareSolverr_PausesAsBefore()
    {
        var tokens = await SessionTokens();
        var fs = new RouteHandler(_ => Json(HttpStatusCode.OK, FsOk));
        MmfFlareSolverr.HandlerOverride = fs;
        MmfScraperPlugin.ApiHandlerOverride = new RouteHandler(_ => Html(HttpStatusCode.Forbidden, "<html>Just a moment...</html>"));
        var r = await new MmfScraperPlugin().ScrapeModelAsync(Ctx(tokens, new() { ["DELAY_MS"] = "0" }), Model());
        Assert.True(r.AuthExpired);
        Assert.Empty(fs.Requests);
        Assert.Equal("PHPSESSID=keepme; cf_clearance=old", await tokens.GetTokenAsync(MmfSession.CookieKey));
    }

    [Fact]
    public async Task FlareSolverr_WithoutClearance_ReturnsNull_SessionUntouched()
    {
        var tokens = await SessionTokens();
        MmfFlareSolverr.HandlerOverride = new RouteHandler(_ => Json(HttpStatusCode.OK,
            "{\"status\":\"error\",\"message\":\"Challenge not solved\"}"));
        var ctx = Ctx(tokens, new() { ["FLARESOLVERR_URL"] = "http://fs.invalid" });
        var creds = new MmfCredentials("PHPSESSID=keepme", null, "UA/0");
        Assert.Null(await MmfFlareSolverr.RefreshAsync(ctx, creds, default));
        Assert.Equal("PHPSESSID=keepme; cf_clearance=old", await tokens.GetTokenAsync(MmfSession.CookieKey));
    }

    // ---------- skip list ----------

    [Fact]
    public void SkipReport_WritesJsonAndCsv()
    {
        var recs = new List<SyncSkipRecord>
        {
            new(3, "200", "Haito", "Main, Base \"25mm\"", "duplicate manifest row"),
            new(4, "201", null, "X", "not new (already in library)"),
        };
        var json = SyncSkipRecord.WriteReport(_dir, "skipped-mmf-test", recs);
        var back = JsonSerializer.Deserialize<List<SyncSkipRecord>>(File.ReadAllText(json))!;
        Assert.Equal(2, back.Count);
        Assert.Equal("Haito", back[0].Creator);
        var csv = File.ReadAllLines(Path.Combine(_dir, "skipped-mmf-test.csv"));
        Assert.Equal("index,id,creator,title,reason", csv[0]);
        Assert.Equal("3,200,Haito,\"Main, Base \"\"25mm\"\"\",duplicate manifest row", csv[1]);
    }
}
