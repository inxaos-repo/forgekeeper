using System.IO.Compression;
using System.Net;
using System.Text;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Services;
using Forgekeeper.PluginSdk;
using Forgekeeper.Scraper.Mmf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>Phase 2 — session auth, session downloads, New-only selection, stale-run cleanup. No real MMF calls.</summary>
[Collection("MmfApiHandlerOverride")]
public class MmfPhase2Tests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fk-p2-").FullName;
    private readonly List<TimeSpan> _delays = new();

    public MmfPhase2Tests()
    {
        MmfScraperPlugin.DelayOverride = (t, _) => { lock (_delays) _delays.Add(t); return Task.CompletedTask; };
    }

    public void Dispose()
    {
        MmfScraperPlugin.ApiHandlerOverride = null;
        MmfScraperPlugin.DelayOverride = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---------- helpers ----------

    private static byte[] MakeZip()
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var e = z.CreateEntry("model/part.stl");
            using var w = new StreamWriter(e.Open());
            w.Write("solid x\nendsolid x\n");
        }
        return ms.ToArray();
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Redirect(string to)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(to);
        return r;
    }

    private static HttpResponseMessage Bytes(byte[] b)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };
        r.Content.Headers.ContentType = new("application/zip");
        return r;
    }

    private PluginContext Ctx(ITokenStore tokens, Dictionary<string, string>? config = null) => new()
    {
        SourceDirectory = _dir,
        ModelDirectory = Path.Combine(_dir, "creator", "model"),
        Config = config ?? new Dictionary<string, string> { ["DELAY_MS"] = "0", ["DOWNLOAD_DELAY_MS"] = "0" },
        HttpClient = new HttpClient(new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
        Logger = NullLogger.Instance,
        TokenStore = tokens,
        Progress = new Progress<ScrapeProgress>(),
    };

    private static async Task<InMemoryTokenStore> SessionTokens(string? bearer = null)
    {
        var t = new InMemoryTokenStore();
        await t.SaveTokenAsync(MmfSession.CookieKey, "sessionid=abc; csrftoken=xyz");
        await t.SaveTokenAsync(MmfSession.StateKey, "ok");
        if (bearer != null) await t.SaveTokenAsync("access_token", bearer);
        return t;
    }

    private static ScrapedModel Model(string id = "200", string name = "Cool Dragon") =>
        new() { ExternalId = id, Name = name, CreatorName = "creator" };

    private const string ObjectJson =
        "{\"id\":200,\"name\":\"Cool Dragon\",\"archive_download_url\":\"https://www.myminifactory.com/download/200?archive_id=9\"}";

    // ---------- cookie import ----------

    [Theory]
    [InlineData("Cookie: sessionid=abc; csrftoken=xyz", "sessionid=abc; csrftoken=xyz")]
    [InlineData("sessionid=abc;csrftoken=xyz;sessionid=new", "sessionid=new; csrftoken=xyz")]
    [InlineData("[{\"name\":\"sessionid\",\"value\":\"abc\",\"domain\":\".myminifactory.com\"},{\"name\":\"x\",\"value\":\"1\",\"domain\":\"evil.com\"}]", "sessionid=abc")]
    [InlineData("{\"cookies\":[{\"name\":\"cf_clearance\",\"value\":\"q\",\"domain\":\"www.myminifactory.com\"}],\"origins\":[]}", "cf_clearance=q")]
    public void Normalize_AcceptsHeaderJsonAndStorageState(string input, string expected) =>
        Assert.Equal(expected, MmfSession.NormalizeCookieInput(input));

    [Fact]
    public async Task Import_StoresSession_AndAuthenticateUsesIt()
    {
        var tokens = new InMemoryTokenStore();
        var r = await new MmfScraperPlugin().HandleAuthCallbackAsync(Ctx(tokens),
            new Dictionary<string, string> { ["session_cookies"] = "sessionid=abc", ["user_agent"] = "UA/1" });
        Assert.True(r.Authenticated);
        Assert.Equal("sessionid=abc", await tokens.GetTokenAsync(MmfSession.CookieKey));
        Assert.Equal("ok", await tokens.GetTokenAsync(MmfSession.StateKey));
        Assert.DoesNotContain("abc", r.Message ?? "");
        Assert.True((await new MmfScraperPlugin().AuthenticateAsync(Ctx(tokens))).Authenticated);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "text/html", "<html>Just a moment...</html>", MmfResponseKind.CloudflareChallenge)]
    [InlineData(HttpStatusCode.Forbidden, "application/json", "{\"detail\":\"no\"}", MmfResponseKind.AuthExpired)]
    [InlineData(HttpStatusCode.Unauthorized, "application/json", "{}", MmfResponseKind.AuthExpired)]
    [InlineData(HttpStatusCode.TooManyRequests, "application/json", "{}", MmfResponseKind.RateLimited)]
    public void Classify_DistinguishesCloudflareFromAuth(HttpStatusCode code, string ct, string body, MmfResponseKind expected) =>
        Assert.Equal(expected, MmfSession.Classify(code, ct, body));

    // ---------- session expiry ----------

    [Fact]
    public async Task SessionExpired_OnObjectFetch_ReturnsAuthExpired_AndMarksSession()
    {
        var tokens = await SessionTokens();
        MmfScraperPlugin.ApiHandlerOverride = new RouteHandler(_ => Json(HttpStatusCode.Unauthorized, "{\"detail\":\"auth\"}"));
        var result = await new MmfScraperPlugin().ScrapeModelAsync(Ctx(tokens), Model());
        Assert.False(result.Success);
        Assert.True(result.AuthExpired);
        Assert.Equal("expired", await tokens.GetTokenAsync(MmfSession.StateKey));
        Assert.False((await MmfScraperPlugin.GetCredentialsAsync(Ctx(tokens), default)).HasSession);
    }

    [Fact]
    public async Task SessionExpired_FallsBackToOAuthBearer()
    {
        var tokens = await SessionTokens(bearer: "bearer-1");
        var zip = MakeZip();
        var handler = new RouteHandler(req =>
        {
            var hasCookie = req.Headers.Contains("Cookie");
            var url = req.RequestUri!.ToString();
            if (url.Contains("/api/v2/objects/200"))
                return hasCookie ? Json(HttpStatusCode.Forbidden, "{}") : Json(HttpStatusCode.OK, ObjectJson);
            if (url.Contains("/download/200")) return Redirect("https://files.example-storage.net/a.zip");
            return Bytes(zip);
        });
        MmfScraperPlugin.ApiHandlerOverride = handler;
        var result = await new MmfScraperPlugin().ScrapeModelAsync(Ctx(tokens), Model());
        Assert.True(result.Success, result.Error);
        Assert.Contains(handler.Requests, r => r.Headers.Authorization?.Parameter == "bearer-1");
    }

    [Fact]
    public async Task ManifestFetch_SessionExpired_ThrowsPluginAuthExpired()
    {
        var tokens = await SessionTokens();
        MmfScraperPlugin.ApiHandlerOverride = new RouteHandler(_ => Json(HttpStatusCode.Forbidden, "{\"detail\":\"login\"}"));
        await Assert.ThrowsAsync<PluginAuthExpiredException>(() => new MmfScraperPlugin().FetchManifestAsync(Ctx(tokens), null));
        Assert.Equal("expired", await tokens.GetTokenAsync(MmfSession.StateKey));
    }

    [Fact]
    public async Task ManifestFetch_UsesSessionLibraryEndpoint()
    {
        var tokens = await SessionTokens();
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mmf", "objectPreviews.sample.json"));
        var handler = new RouteHandler(req => req.RequestUri!.AbsolutePath == "/api/data-library/objectPreviews"
            ? Json(HttpStatusCode.OK, fixture) : Json(HttpStatusCode.NotFound, "{}"));
        MmfScraperPlugin.ApiHandlerOverride = handler;
        var models = await new MmfScraperPlugin().FetchManifestAsync(Ctx(tokens), null);
        Assert.NotEmpty(models);
        Assert.Equal("sessionid=abc; csrftoken=xyz", handler.Requests.Single().Headers.GetValues("Cookie").Single());
    }

    // ---------- downloads ----------

    [Fact]
    public async Task Download_FollowsRedirect_StripsCredsOffSite_AndWritesAtomically()
    {
        var tokens = await SessionTokens();
        var zip = MakeZip();
        var handler = new RouteHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/api/v2/objects/200")) return Json(HttpStatusCode.OK, ObjectJson);
            if (url.Contains("/download/200")) return Redirect("https://files.example-storage.net/obj/200.zip?sig=1");
            if (url.StartsWith("https://files.example-storage.net/")) return Bytes(zip);
            return Json(HttpStatusCode.NotFound, "{}");
        });
        MmfScraperPlugin.ApiHandlerOverride = handler;
        var ctx = Ctx(tokens);
        var result = await new MmfScraperPlugin().ScrapeModelAsync(ctx, Model());

        Assert.True(result.Success, result.Error);
        var archive = Path.Combine(ctx.ModelDirectory!, "Cool Dragon.zip");
        Assert.True(File.Exists(archive));
        Assert.Equal(zip.Length, new FileInfo(archive).Length);
        Assert.True(File.Exists(Path.Combine(ctx.ModelDirectory!, "metadata.json")));
        Assert.Empty(Directory.GetFiles(ctx.ModelDirectory!, "*.part"));
        var storageReq = handler.Requests.Single(r => r.RequestUri!.Host == "files.example-storage.net");
        Assert.False(storageReq.Headers.Contains("Cookie"));
        Assert.Null(storageReq.Headers.Authorization);
        Assert.True(handler.Requests.Single(r => r.RequestUri!.AbsolutePath == "/download/200").Headers.Contains("Cookie"));
    }

    [Fact]
    public async Task Download_429_HonoursRetryAfter_ThenSucceeds()
    {
        var zip = MakeZip();
        int calls = 0;
        var handler = new RouteHandler(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                var r = Json(HttpStatusCode.TooManyRequests, "{}");
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return r;
            }
            return Bytes(zip);
        });
        var waits = new List<TimeSpan>();
        var dl = new MmfDownloader(handler, new MmfDownloadOptions { Delay = (t, _) => { waits.Add(t); return Task.CompletedTask; } });
        var path = Path.Combine(_dir, "x.zip");
        var outcome = await dl.DownloadAsync("https://www.myminifactory.com/download/1", path, new MmfCredentials("a=b", null, "UA"));
        Assert.True(outcome.Success);
        Assert.Equal(2, outcome.Attempts);
        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, waits);
    }

    [Fact]
    public async Task Download_429Forever_FailsAfterMaxAttempts_WithBackoff()
    {
        var handler = new RouteHandler(_ => Json(HttpStatusCode.TooManyRequests, "{}"));
        var waits = new List<TimeSpan>();
        var dl = new MmfDownloader(handler, new MmfDownloadOptions { MaxAttempts = 3, Delay = (t, _) => { waits.Add(t); return Task.CompletedTask; } });
        var path = Path.Combine(_dir, "y.zip");
        var outcome = await dl.DownloadAsync("https://www.myminifactory.com/download/1", path, new MmfCredentials("a=b", null, "UA"));
        Assert.Equal(DownloadStatus.Failed, outcome.Status);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8) }, waits);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Download_CorruptOrTruncated_CleansUpPartialFile_AndCountsAsFailure()
    {
        var tokens = await SessionTokens();
        var handler = new RouteHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/api/v2/objects/200")) return Json(HttpStatusCode.OK, ObjectJson);
            if (url.Contains("/download/200")) return Redirect("https://files.example-storage.net/a.zip");
            return Bytes(Encoding.UTF8.GetBytes("PK\u0003\u0004 this is not a whole zip"));
        });
        MmfScraperPlugin.ApiHandlerOverride = handler;
        var ctx = Ctx(tokens);
        var result = await new MmfScraperPlugin().ScrapeModelAsync(ctx, Model());

        Assert.False(result.Success);       // #28: failed download = failure
        Assert.False(result.AuthExpired);
        Assert.Contains("Corrupt zip", result.Error);
        Assert.False(File.Exists(Path.Combine(ctx.ModelDirectory!, "Cool Dragon.zip")));
        Assert.Empty(Directory.GetFiles(ctx.ModelDirectory!, "*.part", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(ctx.ModelDirectory!, ".*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Download_HtmlChallenge_IsCloudflare_NotSuccess()
    {
        var handler = new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        { Content = new StringContent("<html>challenge</html>", Encoding.UTF8, "text/html") });
        var dl = new MmfDownloader(handler, new MmfDownloadOptions { Delay = (_, _) => Task.CompletedTask });
        var outcome = await dl.DownloadAsync("https://www.myminifactory.com/download/1", Path.Combine(_dir, "z.zip"), new MmfCredentials("a=b", null, "UA"));
        Assert.Equal(DownloadStatus.CloudflareChallenge, outcome.Status);
    }

    [Fact]
    public async Task Download_ExistingArchive_IsNotRedownloaded_UnlessForced()
    {
        var tokens = await SessionTokens();
        var zip = MakeZip();
        var handler = new RouteHandler(req => req.RequestUri!.ToString().Contains("/api/v2/objects/")
            ? Json(HttpStatusCode.OK, ObjectJson)
            : req.RequestUri!.AbsolutePath.StartsWith("/download/") ? Redirect("https://files.example-storage.net/a.zip") : Bytes(zip));
        MmfScraperPlugin.ApiHandlerOverride = handler;
        var ctx = Ctx(tokens);
        Directory.CreateDirectory(ctx.ModelDirectory!);
        await File.WriteAllBytesAsync(Path.Combine(ctx.ModelDirectory!, "Cool Dragon.zip"), zip);

        var r = await new MmfScraperPlugin().ScrapeModelAsync(ctx, Model());
        Assert.True(r.Success);
        Assert.DoesNotContain(handler.Requests, x => x.RequestUri!.Host == "files.example-storage.net");
    }

    // ---------- host: New-only selection, MAX_ITEMS, pause, stale runs ----------

    private (PluginHostService Host, IServiceProvider Sp) CreateHost()
    {
        var services = new ServiceCollection();
        var dbName = $"p2_{Guid.NewGuid()}";
        services.AddSingleton<IDbContextFactory<ForgeDbContext>>(new InMemFactory(dbName));
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forgekeeper:PluginsDirectory"] = Path.Combine(_dir, "plugins"),
            ["Forgekeeper:SourcesDirectory"] = Path.Combine(_dir, "sources"),
        }).Build();
        var host = new PluginHostService(sp, new Mock<ILogger<PluginHostService>>().Object, config,
            new ManifestValidationService(new Mock<ILogger<ManifestValidationService>>().Object),
            new SdkCompatibilityChecker(new Mock<ILogger<SdkCompatibilityChecker>>().Object));
        return (host, sp);
    }

    private sealed class InMemFactory(string name) : IDbContextFactory<ForgeDbContext>
    {
        public ForgeDbContext CreateDbContext() => TestDbContextFactory.Create(name);
    }

    private static Mock<ILibraryScraper> FakeScraper(IReadOnlyList<ScrapedModel> manifest, Func<ScrapedModel, ScrapeResult>? scrape = null,
        Exception? manifestError = null)
    {
        var m = new Mock<ILibraryScraper>();
        m.SetupGet(s => s.SourceSlug).Returns("mmf");
        m.SetupGet(s => s.SourceName).Returns("MyMiniFactory");
        m.SetupGet(s => s.ConfigSchema).Returns(new MmfScraperPlugin().ConfigSchema);
        m.Setup(s => s.AuthenticateAsync(It.IsAny<PluginContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuthResult.Success("session"));
        var fetch = m.Setup(s => s.FetchManifestAsync(It.IsAny<PluginContext>(), It.IsAny<Stream?>(), It.IsAny<CancellationToken>()));
        if (manifestError != null) fetch.ThrowsAsync(manifestError);
        else fetch.ReturnsAsync(manifest);
        m.Setup(s => s.ScrapeModelAsync(It.IsAny<PluginContext>(), It.IsAny<ScrapedModel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PluginContext _, ScrapedModel sm, CancellationToken _) => scrape?.Invoke(sm) ?? ScrapeResult.Ok("metadata.json", []));
        return m;
    }

    private static async Task SetConfig(IServiceProvider sp, params (string K, string V)[] kv)
    {
        var dbf = sp.GetRequiredService<IDbContextFactory<ForgeDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        foreach (var (k, v) in kv) db.PluginConfigs.Add(new PluginConfig { PluginSlug = "mmf", Key = k, Value = v });
        await db.SaveChangesAsync();
    }

    private string MmfSrc => Path.Combine(_dir, "sources", "mmf");

    private void PutOnDiskById(string creator, string name, string id)
    {
        var d = Path.Combine(MmfSrc, creator, name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "metadata.json"), $"{{\"externalId\":\"{id}\",\"source\":\"mmf\",\"name\":\"{name}\"}}");
        File.WriteAllText(Path.Combine(d, "a.stl"), "solid");
    }

    [Fact]
    public async Task NewOnly_SkipsItemsOnDisk_AndDuplicates_ByDefault()
    {
        var (host, sp) = CreateHost();
        PutOnDiskById("alice", "Old Thing", "100");
        var manifest = new List<ScrapedModel>
        {
            new() { ExternalId = "100", Name = "Old Thing", CreatorName = "alice" },
            new() { ExternalId = "101", Name = "Brand New", CreatorName = "alice" },
            new() { ExternalId = "101", Name = "Brand New", CreatorName = "alice" },
        };
        var scraped = new List<string>();
        var scraper = FakeScraper(manifest, m => { scraped.Add(m.ExternalId); return ScrapeResult.Ok("metadata.json", []); });
        host.RegisterPluginForTest("mmf", scraper.Object);

        await host.RunSyncAsync("mmf", CancellationToken.None);
        Assert.Equal(new[] { "101" }, scraped);
    }

    [Fact]
    public async Task ForceRedownload_IncludesOnDiskItems()
    {
        var (host, sp) = CreateHost();
        PutOnDiskById("alice", "Old Thing", "100");
        await SetConfig(sp, ("FORCE_REDOWNLOAD", "true"));
        var scraped = new List<string>();
        var scraper = FakeScraper(new List<ScrapedModel> { new() { ExternalId = "100", Name = "Old Thing", CreatorName = "alice" } },
            m => { scraped.Add(m.ExternalId); return ScrapeResult.Ok("metadata.json", []); });
        host.RegisterPluginForTest("mmf", scraper.Object);
        await host.RunSyncAsync("mmf", CancellationToken.None);
        Assert.Equal(new[] { "100" }, scraped);
    }

    [Fact]
    public async Task MaxItems_StopsAndCheckpoints_ThenResumeContinues()
    {
        var (host, sp) = CreateHost();
        await SetConfig(sp, ("MAX_ITEMS", "2"));
        var manifest = Enumerable.Range(1, 5).Select(i => new ScrapedModel { ExternalId = $"{i}", Name = $"M{i}", CreatorName = "c" }).ToList();
        var scraped = new List<string>();
        var scraper = FakeScraper(manifest, m => { scraped.Add(m.ExternalId); return ScrapeResult.Ok("metadata.json", []); });
        host.RegisterPluginForTest("mmf", scraper.Object);

        await host.RunSyncAsync("mmf", CancellationToken.None);
        Assert.Equal(new[] { "1", "2" }, scraped);
        var dbf = sp.GetRequiredService<IDbContextFactory<ForgeDbContext>>();
        int idx;
        await using (var db = await dbf.CreateDbContextAsync())
        {
            var run = await db.SyncRuns.SingleAsync();
            Assert.Equal("paused", run.Status);
            idx = run.LastProcessedIndex;
            Assert.Equal(2, idx);
        }
        await host.RunSyncAsync("mmf", CancellationToken.None, idx);
        Assert.Equal(new[] { "1", "2", "3", "4" }, scraped);
    }

    [Fact]
    public async Task ManifestAuthExpired_PausesWithReconnectNeeded()
    {
        var (host, sp) = CreateHost();
        var scraper = FakeScraper([], manifestError: new PluginAuthExpiredException("MMF session expired — reconnect needed"));
        host.RegisterPluginForTest("mmf", scraper.Object);
        await host.RunSyncAsync("mmf", CancellationToken.None);
        var status = host.GetSyncStatus("mmf")!;
        Assert.True(status.NeedsReauth);
        var dbf = sp.GetRequiredService<IDbContextFactory<ForgeDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        Assert.Equal("paused", (await db.SyncRuns.SingleAsync()).Status);
    }

    [Fact]
    public async Task ScrapeAuthExpired_PausesAtCheckpoint()
    {
        var (host, sp) = CreateHost();
        var manifest = Enumerable.Range(1, 3).Select(i => new ScrapedModel { ExternalId = $"{i}", Name = $"M{i}", CreatorName = "c" }).ToList();
        var scraper = FakeScraper(manifest, m => m.ExternalId == "2" ? ScrapeResult.TokenExpired("expired") : ScrapeResult.Ok("metadata.json", []));
        host.RegisterPluginForTest("mmf", scraper.Object);
        await host.RunSyncAsync("mmf", CancellationToken.None);
        Assert.True(host.GetSyncStatus("mmf")!.NeedsReauth);
        var dbf = sp.GetRequiredService<IDbContextFactory<ForgeDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        var run = await db.SyncRuns.SingleAsync();
        Assert.Equal("paused", run.Status);
        Assert.Equal(1, run.LastProcessedIndex);
    }

    [Fact]
    public async Task StaleRunningRuns_AreMarkedFailedAtStartup()
    {
        var (host, sp) = CreateHost();
        var dbf = sp.GetRequiredService<IDbContextFactory<ForgeDbContext>>();
        await using (var db = await dbf.CreateDbContextAsync())
        {
            db.SyncRuns.Add(new SyncRun { PluginSlug = "mmf", Status = "running", LastProcessedIndex = 42, StartedAt = DateTime.UtcNow.AddHours(-3) });
            db.SyncRuns.Add(new SyncRun { PluginSlug = "mmf", Status = "completed", StartedAt = DateTime.UtcNow.AddHours(-5) });
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await host.MarkStaleRunsAsync(CancellationToken.None));
        await using (var db = await dbf.CreateDbContextAsync())
        {
            var stale = await db.SyncRuns.SingleAsync(r => r.LastProcessedIndex == 42);
            Assert.Equal("failed", stale.Status);
            Assert.Contains("Abandoned", stale.Error);
            Assert.NotNull(stale.CompletedAt);
            Assert.Equal(1, await db.SyncRuns.CountAsync(r => r.Status == "completed"));
        }
    }
}
