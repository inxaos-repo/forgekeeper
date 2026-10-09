using System.Text.Json;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Services;
using Forgekeeper.PluginSdk;
using Forgekeeper.Scraper.Mmf;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>#37 (Local DateTime → timestamptz) and #38 (sync_runs download counters / no-link skip).</summary>
public class UtcAndSyncCounterTests
{
    [Fact]
    public void Normalize_Local_BecomesUtc()
    {
        var local = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Local);
        var n = UtcDateTime.Normalize(local);
        Assert.Equal(DateTimeKind.Utc, n.Kind);
        Assert.Equal(local.ToUniversalTime(), n);
    }

    [Fact]
    public void Normalize_Unspecified_AssumedUtc_AndUtcUnchanged()
    {
        var u = new DateTime(2026, 1, 1, 5, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal(DateTimeKind.Utc, UtcDateTime.Normalize(u).Kind);
        Assert.Equal(5, UtcDateTime.Normalize(u).Hour);
        var utc = DateTime.UtcNow;
        Assert.Equal(utc, UtcDateTime.Normalize(utc));
        Assert.Null(UtcDateTime.Normalize((DateTime?)null));
    }

    [Fact]
    public void JsonOffsetDate_ParsesAsLocal_ConverterFixesIt()
    {
        // System.Text.Json yields Kind=Local for offset timestamps — the root of #37.
        var parsed = JsonSerializer.Deserialize<DateTime>("\"2025-03-01T10:00:00+02:00\"");
        Assert.Equal(DateTimeKind.Local, parsed.Kind);
        var conv = new UtcNullableDateTimeConverter();
        var stored = (DateTime?)conv.ConvertToProvider(parsed);
        Assert.Equal(DateTimeKind.Utc, stored!.Value.Kind);
        Assert.Equal(new DateTime(2025, 3, 1, 8, 0, 0, DateTimeKind.Utc), stored.Value);
    }

    [Fact]
    public void DbContext_SavesModelWithLocalDates_AsUtc()
    {
        using var db = TestDbContextFactory.Create();
        var creator = new Forgekeeper.Core.Models.Creator { Id = Guid.NewGuid(), Name = "Scoutsifer Studio" };
        db.Creators.Add(creator);
        var m = new Forgekeeper.Core.Models.Model3D
        {
            Id = Guid.NewGuid(), Name = "Rig", CreatorId = creator.Id, BasePath = "/library/x",
            DownloadedAt = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Local),
        };
        db.Models.Add(m);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        var back = db.Models.Single(x => x.Id == m.Id);
        Assert.Equal(DateTimeKind.Utc, back.DownloadedAt!.Value.Kind);
    }

    [Fact]
    public void CountDownloaded_SumsSizes_FallsBackToDisk()
    {
        var tmp = Path.GetTempFileName();
        File.WriteAllBytes(tmp, new byte[123]);
        try
        {
            var (n, b) = PluginHostService.CountDownloaded(new List<DownloadedFile>
            {
                new() { Filename = "a.zip", LocalPath = "/nonexistent/a.zip", Size = 1000 },
                new() { Filename = "b.stl", LocalPath = tmp, Size = 0 },
            });
            Assert.Equal(2, n);
            Assert.Equal(1123, b);
            Assert.Equal((0, 0L), PluginHostService.CountDownloaded(null));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void ScrapeResult_Skip_IsNotSuccess_AndCarriesReason()
    {
        var r = ScrapeResult.Skip("no files");
        Assert.False(r.Success);
        Assert.True(r.Skipped);
        Assert.False(r.AuthExpired);
        Assert.Equal("no files", r.Error);
    }

    [Fact]
    public void DescribeFilesShape_ReportsKeysOnly()
    {
        using var d1 = JsonDocument.Parse("{\"id\":1,\"files\":{\"total_count\":0,\"items\":[]}}");
        Assert.Equal("files{total_count,items} items=0 withDownloadUrl=0", MmfScraperPlugin.DescribeFilesShape(d1.RootElement));
        using var d2 = JsonDocument.Parse("{\"id\":1}");
        Assert.Equal("files=absent", MmfScraperPlugin.DescribeFilesShape(d2.RootElement));
    }
}
