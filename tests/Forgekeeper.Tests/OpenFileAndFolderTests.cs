using System.Net;
using System.Text.Json;
using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Models;
using Forgekeeper.Core.Services;
using Forgekeeper.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>Open file / open folder: Windows path mapping, file:// and protocol links, safe streaming.</summary>
public class LibraryPathMapperTests
{
    private const string Unc = @"\\samba.k8s.inxaos.com\warehousepool\3d printing";

    [Theory]
    [InlineData("/library/sources/mmf/Foo/bar.stl", @"\\samba.k8s.inxaos.com\warehousepool\3d printing\sources\mmf\Foo\bar.stl")]
    [InlineData("/library", @"\\samba.k8s.inxaos.com\warehousepool\3d printing")]
    [InlineData("/library/", @"\\samba.k8s.inxaos.com\warehousepool\3d printing")]
    [InlineData("/library/a/./b/../c.stl", @"\\samba.k8s.inxaos.com\warehousepool\3d printing\a\c.stl")]
    public void Maps_paths_under_root(string linux, string expected)
        => Assert.Equal(expected, LibraryPathMapper.ToWindowsPath(linux, "/library", Unc));

    [Theory]
    [InlineData("/libraryX/foo.stl")]
    [InlineData("/data/plugins/x")]
    [InlineData("/library/../etc/passwd")]
    [InlineData("relative/path.stl")]
    [InlineData("")]
    public void Does_not_map_paths_outside_root(string linux)
        => Assert.Null(LibraryPathMapper.ToWindowsPath(linux, "/library", Unc));

    [Fact]
    public void No_prefix_means_no_mapping()
        => Assert.Null(LibraryPathMapper.ToWindowsPath("/library/a.stl", "/library", null));

    [Fact]
    public void Prefix_trailing_slash_and_forward_slashes_are_normalized()
    {
        Assert.Equal(@"W:\3d printing\a\b.stl", LibraryPathMapper.ToWindowsPath("/library/a/b.stl", "/library/", "W:/3d printing/"));
        Assert.Equal(@"W:\", LibraryPathMapper.ToWindowsPath("/library", "/library", @"W:\"));
    }

    [Fact]
    public void File_url_for_unc_and_drive()
    {
        Assert.Equal("file://samba.k8s.inxaos.com/warehousepool/3d%20printing/a%23b.stl",
            LibraryPathMapper.ToFileUrl(@"\\samba.k8s.inxaos.com\warehousepool\3d printing\a#b.stl"));
        Assert.Equal("file:///W:/3d%20printing/x.stl", LibraryPathMapper.ToFileUrl(@"w:\3d printing\x.stl"));
        Assert.Null(LibraryPathMapper.ToFileUrl(null));
        Assert.Null(LibraryPathMapper.ToFileUrl("/library/x"));
    }

    [Fact]
    public void Protocol_url_encodes_path()
    {
        var url = LibraryPathMapper.ToProtocolUrl(@"W:\a b&c\x.stl", select: true)!;
        Assert.StartsWith("forgekeeper-open://select?path=", url);
        Assert.Equal(@"W:\a b&c\x.stl", Uri.UnescapeDataString(url.Split("path=")[1]));
        Assert.StartsWith("forgekeeper-open://open?", LibraryPathMapper.ToProtocolUrl(@"W:\a", select: false));
    }

    [Fact]
    public void IsWithinRoots_blocks_traversal_and_prefix_tricks()
    {
        Assert.True(LibraryPathMapper.IsWithinRoots("/library/a/b.stl", ["/library"]));
        Assert.True(LibraryPathMapper.IsWithinRoots("/library", ["/library/"]));
        Assert.False(LibraryPathMapper.IsWithinRoots("/library/../etc/passwd", ["/library"]));
        Assert.False(LibraryPathMapper.IsWithinRoots("/library2/x", ["/library"]));
        Assert.False(LibraryPathMapper.IsWithinRoots("/etc/passwd", [null, "", "/library"]));
    }
}

public class OpenFileTestFactory : ForgeTestFactory
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"fk-open-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(Root);
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:BasePaths:0"] = Root,
            ["Library:WindowsPathPrefix"] = @"\\samba.k8s.inxaos.com\warehousepool\3d printing",
        }));
    }
}

public class OpenFileEndpointTests : IClassFixture<OpenFileTestFactory>
{
    private readonly OpenFileTestFactory _factory;
    private readonly HttpClient _client;

    public OpenFileEndpointTests(OpenFileTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<(Guid modelId, Guid variantId)> Seed(string basePath, string filePath, string fileName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var creator = new Creator { Id = Guid.NewGuid(), Name = $"C_{Guid.NewGuid():N}", Source = SourceType.Manual };
        var model = new Model3D
        {
            Id = Guid.NewGuid(), Name = "Open Me", CreatorId = creator.Id, Creator = creator,
            BasePath = basePath, Source = SourceType.Manual,
        };
        var variant = new Variant
        {
            Id = Guid.NewGuid(), ModelId = model.Id, Model = model, FilePath = filePath, FileName = fileName,
            FileType = FileType.Stl, VariantType = VariantType.Unsupported,
        };
        db.Creators.Add(creator);
        db.Models.Add(model);
        db.Variants.Add(variant);
        await db.SaveChangesAsync();
        return (model.Id, variant.Id);
    }

    [Fact]
    public async Task Detail_includes_windows_paths_but_no_explorer_links_by_default()
    {
        var dir = Path.Combine(_factory.Root, "Creator", "Model");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "part.stl"), "solid x");
        var (modelId, _) = await Seed(dir, "part.stl", "part.stl");

        var json = JsonDocument.Parse(await _client.GetStringAsync($"/api/v1/models/{modelId}")).RootElement;
        Assert.Equal(@"\\samba.k8s.inxaos.com\warehousepool\3d printing\Creator\Model", json.GetProperty("windowsBasePath").GetString());
        Assert.True(IsNullOrMissing(json, "baseFileUrl"));
        Assert.True(IsNullOrMissing(json, "openFolderUrl"));

        var v = json.GetProperty("variants")[0];
        Assert.Equal(Path.Combine(dir, "part.stl"), v.GetProperty("absolutePath").GetString());
        Assert.Equal(@"\\samba.k8s.inxaos.com\warehousepool\3d printing\Creator\Model\part.stl", v.GetProperty("windowsPath").GetString());
        Assert.True(IsNullOrMissing(v, "revealUrl"));
        Assert.True(IsNullOrMissing(v, "fileUrl"));
    }

    internal static bool IsNullOrMissing(JsonElement e, string name)
        => !e.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null;

    [Fact]
    public async Task Download_streams_attachment_and_inline()
    {
        var dir = Path.Combine(_factory.Root, "C2", "M2");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "a.stl"), "solid a");
        var (_, variantId) = await Seed(dir, "a.stl", "a.stl");

        var att = await _client.GetAsync($"/api/v1/variants/{variantId}/download");
        Assert.Equal(HttpStatusCode.OK, att.StatusCode);
        Assert.Equal("solid a", await att.Content.ReadAsStringAsync());
        Assert.Equal("attachment", att.Content.Headers.ContentDisposition?.DispositionType);

        var inl = await _client.GetAsync($"/api/v1/variants/{variantId}/download?inline=true");
        Assert.Equal(HttpStatusCode.OK, inl.StatusCode);
        Assert.Null(inl.Content.Headers.ContentDisposition);
    }

    [Fact]
    public async Task Download_refuses_traversal_out_of_model_folder()
    {
        var dir = Path.Combine(_factory.Root, "C3", "M3");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(_factory.Root, "secret.stl"), "nope");
        var (_, variantId) = await Seed(dir, "../../secret.stl", "secret.stl");

        var res = await _client.GetAsync($"/api/v1/variants/{variantId}/download");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Download_refuses_model_outside_library_roots()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"fk-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "x.stl"), "x");
        var (_, variantId) = await Seed(outside, "x.stl", "x.stl");

        var res = await _client.GetAsync($"/api/v1/variants/{variantId}/download");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Path_mapping_endpoint_reports_config()
    {
        var json = JsonDocument.Parse(await _client.GetStringAsync("/api/v1/files/path-mapping")).RootElement;
        Assert.True(json.GetProperty("configured").GetBoolean());
        Assert.Equal(_factory.Root, json.GetProperty("linuxRoot").GetString());
        Assert.False(json.GetProperty("explorerProtocolEnabled").GetBoolean());
        Assert.True(OpenFileEndpointTests.IsNullOrMissing(json, "protocolScheme"));
    }
}

public class ExplorerEnabledTestFactory : OpenFileTestFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Library:EnableExplorerProtocol"] = "true",
        }));
    }
}

public class ExplorerProtocolEnabledTests : IClassFixture<ExplorerEnabledTestFactory>
{
    private readonly ExplorerEnabledTestFactory _factory;
    private readonly HttpClient _client;

    public ExplorerProtocolEnabledTests(ExplorerEnabledTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Detail_includes_explorer_links_when_enabled()
    {
        var dir = Path.Combine(_factory.Root, "CE", "ME");
        Directory.CreateDirectory(dir);
        Guid modelId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var creator = new Creator { Id = Guid.NewGuid(), Name = $"C_{Guid.NewGuid():N}", Source = SourceType.Manual };
            var model = new Model3D { Id = Guid.NewGuid(), Name = "E", CreatorId = creator.Id, Creator = creator, BasePath = dir, Source = SourceType.Manual };
            var variant = new Variant { Id = Guid.NewGuid(), ModelId = model.Id, Model = model, FilePath = "p.stl", FileName = "p.stl", FileType = FileType.Stl, VariantType = VariantType.Unsupported };
            db.Creators.Add(creator); db.Models.Add(model); db.Variants.Add(variant);
            await db.SaveChangesAsync();
            modelId = model.Id;
        }

        var json = JsonDocument.Parse(await _client.GetStringAsync($"/api/v1/models/{modelId}")).RootElement;
        Assert.Equal("file://samba.k8s.inxaos.com/warehousepool/3d%20printing/CE/ME", json.GetProperty("baseFileUrl").GetString());
        Assert.StartsWith("forgekeeper-open://open?path=", json.GetProperty("openFolderUrl").GetString());
        Assert.StartsWith("forgekeeper-open://select?path=", json.GetProperty("variants")[0].GetProperty("revealUrl").GetString());

        var map = JsonDocument.Parse(await _client.GetStringAsync("/api/v1/files/path-mapping")).RootElement;
        Assert.True(map.GetProperty("explorerProtocolEnabled").GetBoolean());
        Assert.Equal("forgekeeper-open", map.GetProperty("protocolScheme").GetString());
    }
}
