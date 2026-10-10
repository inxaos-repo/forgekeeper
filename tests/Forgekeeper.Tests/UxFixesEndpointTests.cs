using System.Net;
using System.Text.Json;
using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Models;
using Forgekeeper.Core.Services;
using Forgekeeper.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>Tests for #64 (preview image route) and #69 (printed toggle).</summary>
public class UxFixesEndpointTests : IClassFixture<ForgeTestFactory>
{
    private readonly ForgeTestFactory _factory;
    private readonly HttpClient _client;

    public UxFixesEndpointTests(ForgeTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private async Task<Guid> Seed(string basePath, List<string> previews)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var creator = new Creator { Id = Guid.NewGuid(), Name = $"C_{Guid.NewGuid():N}", Source = SourceType.Manual };
        db.Creators.Add(creator);
        var model = new Model3D
        {
            Id = Guid.NewGuid(), Name = "M", CreatorId = creator.Id, Source = SourceType.Manual,
            BasePath = basePath, PreviewImages = previews,
        };
        db.Models.Add(model);
        await db.SaveChangesAsync();
        return model.Id;
    }

    [Fact]
    public void Resolver_RelativePath_ResolvesUnderBase()
    {
        var r = PreviewImageResolver.Resolve("/lib/a b", "sub dir/x.jpg");
        Assert.NotNull(r);
        Assert.Equal(Path.GetFullPath("/lib/a b/sub dir/x.jpg"), r!.LocalPath);
    }

    [Fact]
    public void Resolver_BlocksTraversal()
    {
        Assert.Null(PreviewImageResolver.Resolve("/lib/a", "../../etc/passwd"));
        Assert.Null(PreviewImageResolver.Resolve("/lib/a", "/etc/passwd"));
    }

    [Fact]
    public void Resolver_RemoteUrl_ReturnsUrl()
    {
        var r = PreviewImageResolver.Resolve("/lib/a", "https://dl.myminifactory.com/x.jpg");
        Assert.Equal("https://dl.myminifactory.com/x.jpg", r!.RemoteUrl);
    }

    [Fact]
    public async Task Images_ServesLocalFile_WithImageMime()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fk-img-" + Guid.NewGuid().ToString("N"), "Sub Dir");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "Pic A.jpg"), [0xFF, 0xD8, 0xFF, 0xE0]);
        var id = await Seed(Path.GetDirectoryName(dir)!, ["Sub Dir/Pic A.jpg", "missing.png"]);

        var ok = await _client.GetAsync($"/api/v1/models/{id}/images/0");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("image/jpeg", ok.Content.Headers.ContentType?.MediaType);

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/models/{id}/images/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/models/{id}/images/5")).StatusCode);
    }

    [Fact]
    public async Task Images_RemoteUrl_Redirects()
    {
        var id = await Seed("/nonexistent", ["https://example.com/a.jpg"]);
        var resp = await _client.GetAsync($"/api/v1/models/{id}/images/0");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("https://example.com/a.jpg", resp.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Printed_Toggle_RoundTrips()
    {
        var id = await Seed("/nonexistent", []);
        var on = await _client.PostAsync($"/api/v1/models/{id}/printed", null);
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        // Idempotent
        await _client.PostAsync($"/api/v1/models/{id}/printed", null);

        var json = JsonDocument.Parse(await _client.GetStringAsync($"/api/v1/models/{id}")).RootElement;
        Assert.True(json.GetProperty("printed").GetBoolean());
        Assert.Equal(1, json.GetProperty("printHistory").GetArrayLength());

        var off = await _client.DeleteAsync($"/api/v1/models/{id}/printed");
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        json = JsonDocument.Parse(await _client.GetStringAsync($"/api/v1/models/{id}")).RootElement;
        Assert.False(json.GetProperty("printed").GetBoolean());
    }
}
