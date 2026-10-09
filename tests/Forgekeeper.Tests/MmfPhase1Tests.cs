using System.Net;
using System.Text;
using System.Text.Json;
using Forgekeeper.Core.Enums;
using Forgekeeper.Infrastructure.Services;
using Forgekeeper.PluginSdk;
using Forgekeeper.Scraper.Mmf;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Phase 1 (MMF quick unblock) regression tests, driven by the redacted fixture
/// Fixtures/Mmf/objectPreviews.sample.json (13 rows → 12 distinct → 11 unique ids).
/// </summary>
[Collection("MmfApiHandlerOverride")]
public class MmfPhase1Tests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mmf", "objectPreviews.sample.json");
    private static (IReadOnlyList<ScrapedModel> Models, MmfManifestStats Stats) ParseFixture() =>
        MmfManifestParser.Parse(File.ReadAllText(FixturePath));

    // ── D1: creator fields ──────────────────────────────────────────────

    [Fact]
    public void D1_NoRowParsesWithNullCreator()
    {
        var (models, stats) = ParseFixture();
        Assert.Equal(0, stats.RowsWithoutCreator);
        Assert.All(models, m => Assert.False(string.IsNullOrWhiteSpace(m.CreatorName), $"{m.ExternalId} has no creator"));
    }

    [Fact]
    public void D1_ReadsTopLevelCreatorFields()
    {
        var m = ParseFixture().Models.Single(x => x.ExternalId == "object-200");
        Assert.Equal("Redacted Creator B", m.CreatorName);
        Assert.Equal("1002", m.CreatorId);
        Assert.Equal("redacted-b", m.CreatorUsername);
    }

    [Fact]
    public void D1_FallsBackToLegacyDesignerObject()
    {
        var m = ParseFixture().Models.Single(x => x.ExternalId == "object-600");
        Assert.Equal("Redacted Legacy Creator", m.CreatorName);
        Assert.Equal("1004", m.CreatorId);
        Assert.Equal("redacted-legacy", m.CreatorUsername);
    }

    // ── D2: dedupe ──────────────────────────────────────────────────────

    [Fact]
    public void D2_DedupeCounts_RowsDistinctUnique()
    {
        var (models, stats) = ParseFixture();
        Assert.Equal(13, stats.Rows);
        Assert.Equal(12, stats.DistinctRows);
        Assert.Equal(11, stats.UniqueItems);
        Assert.Equal(11, models.Count);
        Assert.Equal(models.Count, models.Select(m => m.ExternalId).Distinct().Count());
    }

    [Fact]
    public void D2_ObjectAndBundleWithSameNumber_AreDistinctItems()
    {
        var models = ParseFixture().Models;
        var obj = models.Single(m => m.ExternalId == "object-3147");
        var bundle = models.Single(m => m.ExternalId == "bundle-3147");
        Assert.Equal("DOWNLOAD", Assert.Single(obj.Acquisitions).Source);
        Assert.Equal("PURCHASE", Assert.Single(bundle.Acquisitions).Source);
    }

    [Fact]
    public void D2_MergesAcquisitionsAcrossRows()
    {
        var m = ParseFixture().Models.Single(x => x.ExternalId == "object-100");
        Assert.Equal(new[] { "PURCHASE", "THE_ADVENTURE" }, m.Acquisitions.Select(a => a.Source).OrderBy(s => s));
        Assert.Equal("ORD-REDACTED-1", m.Acquisitions.Single(a => a.Source == "PURCHASE").Reference);
        Assert.Equal(new DateTime(2024, 2, 1, 10, 0, 0, DateTimeKind.Utc), m.LibraryAddedAt); // earliest
    }

    [Fact]
    public void D2_BundleRowIsNotItsOwnChild_ChildrenCarryBundleId()
    {
        var models = ParseFixture().Models;
        Assert.Null(models.Single(m => m.ExternalId == "bundle-500").BundleId);
        Assert.Null(models.Single(m => m.ExternalId == "bundle-3147").BundleId);
        Assert.Equal("bundle-500", models.Single(m => m.ExternalId == "object-501").BundleId);
        Assert.Equal("bundle-500", models.Single(m => m.ExternalId == "object-502").BundleId);
        Assert.Null(models.Single(m => m.ExternalId == "object-3147").BundleId);
    }

    [Theory]
    [InlineData("3147", null, "object-3147")]
    [InlineData("3147", "bundle", "bundle-3147")]
    [InlineData("bundle-3147", "object", "bundle-3147")]
    [InlineData("Object-12", null, "object-12")]
    public void D2_NormalizeId_KeepsPrefix(string raw, string? type, string expected) =>
        Assert.Equal(expected, MmfManifestParser.NormalizeId(raw, type));

    // ── D3: acquisition mapping + metadata ──────────────────────────────

    [Theory]
    [InlineData("PURCHASE", "Purchase")]
    [InlineData("TRIBE", "Tribe")]
    [InlineData("FRONTIER", "Campaign")]
    [InlineData("USER_GROUP", "UserGroup")]
    [InlineData("THE_ADVENTURE", "Subscription")]
    [InlineData("DOWNLOAD", "Free")]
    [InlineData("SOMETHING_NEW", "Unknown")]
    public void D3_SourceMapsToAcquisitionMethodEnum(string source, string expected)
    {
        var mapped = MmfManifestParser.MapAcquisitionMethod(source);
        Assert.Equal(expected, mapped);
        Assert.True(Enum.TryParse<AcquisitionMethod>(mapped, out _), $"{mapped} is not an AcquisitionMethod value");
    }

    [Fact]
    public void D3_FixtureCoversEverySourceKind()
    {
        var sources = ParseFixture().Models.SelectMany(m => m.Acquisitions).Select(a => a.Source).ToHashSet();
        foreach (var s in new[] { "PURCHASE", "TRIBE", "FRONTIER", "USER_GROUP", "THE_ADVENTURE", "DOWNLOAD" })
            Assert.Contains(s, sources);
    }

    [Fact]
    public void D3_MetadataContainsAcquisitionsTagsLibraryAddedAtBundleId()
    {
        var m = ParseFixture().Models.Single(x => x.ExternalId == "object-501");
        var meta = MmfScraperPlugin.BuildMetadata(m, null, [], null);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(meta)).RootElement;

        Assert.Equal("object-501", json.GetProperty("externalId").GetString());
        Assert.Equal("bundle-500", json.GetProperty("bundleId").GetString());
        Assert.Equal(JsonValueKind.String, json.GetProperty("libraryAddedAt").ValueKind);
        var acq = json.GetProperty("acquisitions");
        Assert.Equal(1, acq.GetArrayLength());
        Assert.Equal("Purchase", acq[0].GetProperty("method").GetString());
        Assert.Equal("Purchase", json.GetProperty("acquisition").GetProperty("method").GetString());
        Assert.Contains(json.GetProperty("tags").EnumerateArray(), t => t.GetString() == "fantasy");
        Assert.EndsWith("/object/501", json.GetProperty("externalUrl").GetString());
    }

    [Fact]
    public void D3_PrimaryAcquisitionPrefersPurchaseOverSubscription()
    {
        var m = ParseFixture().Models.Single(x => x.ExternalId == "object-100");
        var json = JsonDocument.Parse(JsonSerializer.Serialize(MmfScraperPlugin.BuildMetadata(m, null, [], null))).RootElement;
        Assert.Equal("Purchase", json.GetProperty("acquisition").GetProperty("method").GetString());
        Assert.Equal(2, json.GetProperty("acquisitions").GetArrayLength());
    }

    // ── A2: optional credentials ────────────────────────────────────────

    [Fact]
    public void A2_UsernameAndPasswordAreNotRequired()
    {
        var schema = new MmfScraperPlugin().ConfigSchema;
        Assert.False(schema.Single(f => f.Key == "MMF_USERNAME").Required);
        Assert.False(schema.Single(f => f.Key == "MMF_PASSWORD").Required);
    }

    [Fact]
    public async Task A2_AuthenticateWithoutCredentials_DoesNotFail()
    {
        var ctx = MakeContext(new Dictionary<string, string> { ["CALLBACK_URL"] = "https://fk.example.invalid/auth/mmf/callback" }, new InMemoryTokenStore());
        var result = await new MmfScraperPlugin().AuthenticateAsync(ctx);
        Assert.NotNull(result.AuthUrl); // NeedsBrowser (OAuth implicit flow), not Failed
        Assert.Contains("response_type=token", result.AuthUrl);
    }

    [Fact]
    public async Task A2_AuthenticateWithNothingConfigured_IsManifestOnlyNotFailure()
    {
        var ctx = MakeContext(new Dictionary<string, string>(), new InMemoryTokenStore());
        var plugin = new MmfScraperPlugin();
        if (MmfScraperPlugin.ResolveCallbackUrl(ctx.Config) != null) return; // env provides PublicUrl
        var result = await plugin.AuthenticateAsync(ctx);
        Assert.True(result.Authenticated);
    }

    // ── C1/C2 ───────────────────────────────────────────────────────────

    [Fact]
    public void C2_CallbackUrlDerivedFromPublicUrl()
    {
        var empty = new Dictionary<string, string>();
        Assert.Equal("https://fk.example.invalid/auth/mmf/callback",
            MmfScraperPlugin.ResolveCallbackUrl(empty, _ => "https://fk.example.invalid/"));
        Assert.Equal("https://override.invalid/cb",
            MmfScraperPlugin.ResolveCallbackUrl(new Dictionary<string, string> { ["CALLBACK_URL"] = "https://override.invalid/cb" }, _ => "https://fk.example.invalid"));
        Assert.Null(MmfScraperPlugin.ResolveCallbackUrl(empty, _ => null));
    }

    [Fact]
    public void C2_NoHardCodedSecretOrHostInSchema()
    {
        foreach (var f in new MmfScraperPlugin().ConfigSchema)
        {
            Assert.DoesNotContain("k8s.inxaos.com", f.DefaultValue ?? "");
            Assert.DoesNotMatch("[0-9a-f]{24,}", f.HelpText ?? "");
        }
    }

    [Fact]
    public void C1_ManifestJsonHasNameTagsAndRepoUrls()
    {
        var path = Path.Combine(FindRepoRoot(), "plugins", "Forgekeeper.Scraper.Mmf", "manifest.json");
        var json = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        Assert.Equal("MyMiniFactory Scraper", json.GetProperty("name").GetString());
        Assert.Contains(json.GetProperty("tags").EnumerateArray(), t => t.GetString() == "mmf");
        Assert.Contains("inxaos-repo/forgekeeper", json.GetProperty("homepage").GetString());
    }

    // ── A3: token expiry + failed downloads ─────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "text/html", "<html/>", true)]
    [InlineData(HttpStatusCode.Forbidden, "application/json", "{\"error\":\"invalid_token\"}", true)]
    [InlineData(HttpStatusCode.Forbidden, "text/plain", "{\"error\":\"x\"}", true)]
    [InlineData(HttpStatusCode.Forbidden, "text/html", "<!DOCTYPE html><title>Just a moment...</title>", false)]
    [InlineData(HttpStatusCode.NotFound, "application/json", "{}", false)]
    public async Task A3_TokenExpiredDetection(HttpStatusCode code, string mediaType, string body, bool expected)
    {
        using var resp = new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        Assert.Equal(expected, await MmfScraperPlugin.IsTokenExpiredResponseAsync(resp, default));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "application/json")]
    [InlineData(HttpStatusCode.Forbidden, "application/json")]
    public async Task A3_ExpiredToken_PausesAndClearsToken(HttpStatusCode code, string mediaType)
    {
        var dir = Directory.CreateTempSubdirectory("fk-a3-").FullName;
        var tokens = new InMemoryTokenStore();
        await tokens.SaveTokenAsync("access_token", "expired-token");
        MmfScraperPlugin.ApiHandlerOverride = new StubHandler(_ =>
            new HttpResponseMessage(code) { Content = new StringContent("{\"detail\":\"expired\"}", Encoding.UTF8, mediaType) });
        try
        {
            var ctx = MakeContext(new Dictionary<string, string> { ["DELAY_MS"] = "0" }, tokens, dir);
            var model = ParseFixture().Models.Single(m => m.ExternalId == "object-200");
            var result = await new MmfScraperPlugin().ScrapeModelAsync(ctx, model);

            Assert.False(result.Success);
            Assert.True(result.AuthExpired);
            Assert.Null(await tokens.GetTokenAsync("access_token"));
            // A subsequent auth check asks for the browser again.
            var auth = await new MmfScraperPlugin().AuthenticateAsync(MakeContext(
                new Dictionary<string, string> { ["CALLBACK_URL"] = "https://fk.example.invalid/auth/mmf/callback" }, tokens, dir));
            Assert.NotNull(auth.AuthUrl);
        }
        finally
        {
            MmfScraperPlugin.ApiHandlerOverride = null;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task A3_NoTokenAtAll_PausesInsteadOfSucceeding()
    {
        var dir = Directory.CreateTempSubdirectory("fk-a3-").FullName;
        try
        {
            var ctx = MakeContext(new Dictionary<string, string> { ["DELAY_MS"] = "0" }, new InMemoryTokenStore(), dir);
            var model = ParseFixture().Models.Single(m => m.ExternalId == "object-200");
            var result = await new MmfScraperPlugin().ScrapeModelAsync(ctx, model);
            Assert.True(result.AuthExpired);
            Assert.False(result.Success);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(0, 0, 3, false)] // the 7,324-scraped / 0-bytes bug
    [InlineData(0, 0, 0, true)]  // nothing to download (metadata-only)
    [InlineData(1, 0, 2, true)]  // partial success
    [InlineData(0, 2, 1, true)]  // already on disk
    public void A3_FailedDownloadsAreNotSuccess(int downloaded, int skipped, int failed, bool expected) =>
        Assert.Equal(expected, MmfScraperPlugin.IsScrapeSuccessful(downloaded, skipped, failed));

    // ── D5 / DRY_RUN classification ─────────────────────────────────────

    [Fact]
    public void D5_DryRunClassification_ById_ByName_New_Conflict()
    {
        var root = Directory.CreateTempSubdirectory("fk-d5-").FullName;
        try
        {
            var models = ParseFixture().Models;
            // object-200 lives in unknown/ but is tagged by id → by-id
            WriteMeta(Path.Combine(root, "unknown", "Redacted Dragon"), "object-200");
            // object-300 has a folder by name but no metadata → by-name
            Directory.CreateDirectory(Path.Combine(root, "Redacted Creator B", "Redacted Siege Tower"));
            // bundle-3147's name matches the folder owned by object-3147 → conflict, never treated as on-disk
            WriteMeta(Path.Combine(root, "Redacted Creator A", "Redacted Collision Model"), "object-3147");
            // object-450 claimed by two folders → conflict
            WriteMeta(Path.Combine(root, "Redacted Creator C", "Goblin A"), "object-450");
            WriteMeta(Path.Combine(root, "Redacted Creator C", "Goblin B"), "450");

            var index = new SourceIdIndex();
            index.ScanMetadata(root);
            string? NameMatch(string c, string n) { var p = Path.Combine(root, c, n); return Directory.Exists(p) ? p : null; }
            ReconcileEntry C(string id) => LibraryReconciler.Classify(models.Single(m => m.ExternalId == id), index, root, NameMatch, s => s);

            Assert.Equal(ReconcileStatus.OnDiskById, C("object-200").Status);
            Assert.Contains("unknown", C("object-200").ExistingPath);
            Assert.Equal(ReconcileStatus.OnDiskByName, C("object-300").Status);
            Assert.Equal(ReconcileStatus.OnDiskById, C("object-3147").Status);
            Assert.Equal(ReconcileStatus.Conflict, C("bundle-3147").Status);
            Assert.Equal(ReconcileStatus.Conflict, C("object-450").Status);
            Assert.Equal(ReconcileStatus.New, C("object-600").Status);

            // DB layer wins over metadata.json
            var dbIndex = new SourceIdIndex();
            dbIndex.AddFromDb("object-600", "/library/db-path");
            dbIndex.AddFromMetadata("object-600", "/library/meta-path");
            Assert.Equal("/library/db-path", Assert.Single(dbIndex.Lookup("object-600")));
            Assert.Empty(dbIndex.Lookup("bundle-600"));

            var entries = models.Select(m => LibraryReconciler.Classify(m, index, root, NameMatch, s => s)).ToList();
            var (json, csv) = LibraryReconciler.WriteReport(Path.Combine(root, ".forgekeeper-reports"), "dryrun-test", entries);
            Assert.Equal(12, File.ReadAllLines(csv).Length); // header + 11
            var report = JsonDocument.Parse(File.ReadAllText(json)).RootElement;
            Assert.Equal(11, report.GetProperty("total").GetInt32());
        }
        finally { Directory.Delete(root, true); }
    }

    // ── unknown/ re-home ───────────────────────────────────────────────

    [Fact]
    public void Rehome_PlanIsListingOnly_ApplyMovesOnlyMovable()
    {
        var root = Directory.CreateTempSubdirectory("fk-rehome-").FullName;
        try
        {
            var models = ParseFixture().Models;
            WriteMeta(Path.Combine(root, "unknown", "Redacted Dragon"), "object-200");        // movable
            WriteMeta(Path.Combine(root, "unknown", "Mystery"), "object-999999");             // not in manifest
            Directory.CreateDirectory(Path.Combine(root, "unknown", "No Meta"));               // no metadata
            WriteMeta(Path.Combine(root, "unknown", "Redacted Knight"), "object-100");        // target exists
            Directory.CreateDirectory(Path.Combine(root, "Redacted Creator A", "Redacted Knight"));

            var plan = UnknownRehomePlanner.Plan(root, models, s => s);
            Assert.Equal(4, plan.Count);
            Assert.Equal(RehomeStatus.Movable, plan.Single(p => p.ExternalId == "object-200").Status);
            Assert.Equal(Path.Combine(root, "Redacted Creator B", "Redacted Dragon"), plan.Single(p => p.ExternalId == "object-200").TargetPath);
            Assert.Equal(RehomeStatus.NotInManifest, plan.Single(p => p.ExternalId == "object-999999").Status);
            Assert.Equal(RehomeStatus.NoMetadata, plan.Single(p => p.ExternalId == null).Status);
            Assert.Equal(RehomeStatus.TargetExists, plan.Single(p => p.ExternalId == "object-100").Status);

            // Planning (and writing the report) moved nothing.
            UnknownRehomePlanner.WriteReport(Path.Combine(root, ".forgekeeper-reports"), "rehome", plan);
            Assert.True(Directory.Exists(Path.Combine(root, "unknown", "Redacted Dragon")));

            Assert.Equal(1, UnknownRehomePlanner.Apply(plan));
            Assert.True(Directory.Exists(Path.Combine(root, "Redacted Creator B", "Redacted Dragon")));
            Assert.True(Directory.Exists(Path.Combine(root, "unknown", "Redacted Knight"))); // untouched
        }
        finally { Directory.Delete(root, true); }
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static void WriteMeta(string dir, string externalId)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "metadata.json"), JsonSerializer.Serialize(new { externalId, source = "mmf" }));
    }

    private static PluginContext MakeContext(Dictionary<string, string> config, ITokenStore tokens, string? dir = null)
    {
        dir ??= Path.GetTempPath();
        return new PluginContext
        {
            SourceDirectory = dir,
            ModelDirectory = Path.Combine(dir, "creator", "model"),
            Config = config,
            HttpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
            Logger = NullLogger.Instance,
            TokenStore = tokens,
            Progress = new Progress<ScrapeProgress>(),
        };
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "plugins"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
