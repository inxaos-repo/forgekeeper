using System.IO.Compression;
using System.Net;
using Forgekeeper.Api.Endpoints;
using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Forgekeeper.Tests;

public class ModelZipEndpointTests : IClassFixture<OpenFileTestFactory>
{
    private readonly OpenFileTestFactory _factory;
    private readonly HttpClient _client;

    public ModelZipEndpointTests(OpenFileTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<Guid> Seed(string basePath, params (string path, VariantType type)[] files)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var creator = new Creator { Id = Guid.NewGuid(), Name = $"Maker/{Guid.NewGuid():N}", Source = SourceType.Manual };
        var model = new Model3D
        {
            Id = Guid.NewGuid(), Name = "Big: Dragon", CreatorId = creator.Id, Creator = creator,
            BasePath = basePath, Source = SourceType.Manual,
        };
        db.Creators.Add(creator);
        db.Models.Add(model);
        foreach (var (path, type) in files)
        {
            db.Variants.Add(new Variant
            {
                Id = Guid.NewGuid(), ModelId = model.Id, Model = model, FilePath = path,
                FileName = Path.GetFileName(path), FileType = FileType.Stl, VariantType = type,
            });
        }
        await db.SaveChangesAsync();
        return model.Id;
    }

    private string MakeDir(string name)
    {
        var dir = Path.Combine(_factory.Root, "zip", name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<Dictionary<string, string>> ReadZip(HttpResponseMessage res)
    {
        var bytes = await res.Content.ReadAsByteArrayAsync();
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var result = new Dictionary<string, string>();
        foreach (var e in zip.Entries)
        {
            using var r = new StreamReader(e.Open());
            result[e.FullName] = await r.ReadToEndAsync();
        }
        return result;
    }

    [Fact]
    public async Task Zip_contains_all_files_with_subfolders()
    {
        var dir = MakeDir("all");
        Directory.CreateDirectory(Path.Combine(dir, "Supported"));
        await File.WriteAllTextAsync(Path.Combine(dir, "body.stl"), "solid body");
        await File.WriteAllTextAsync(Path.Combine(dir, "Supported", "body.stl"), "solid sup");
        var id = await Seed(dir, ("body.stl", VariantType.Unsupported), ("Supported/body.stl", VariantType.Supported));

        var res = await _client.GetAsync($"/api/v1/models/{id}/download.zip");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/zip", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", res.Content.Headers.ContentDisposition?.DispositionType);
        var fn = res.Content.Headers.ContentDisposition?.FileNameStar ?? res.Content.Headers.ContentDisposition?.FileName;
        Assert.NotNull(fn);
        Assert.EndsWith(".zip", fn!.Trim('"'));
        Assert.DoesNotContain("/", fn);
        Assert.DoesNotContain(":", fn);

        var entries = await ReadZip(res);
        Assert.Equal("solid body", entries["body.stl"]);
        Assert.Equal("solid sup", entries["Supported/body.stl"]);
        Assert.DoesNotContain("_MISSING_FILES.txt", entries.Keys);
    }

    [Fact]
    public async Task Zip_filters_by_variant_type_case_insensitively()
    {
        var dir = MakeDir("filter");
        await File.WriteAllTextAsync(Path.Combine(dir, "a.stl"), "a");
        await File.WriteAllTextAsync(Path.Combine(dir, "b.stl"), "b");
        var id = await Seed(dir, ("a.stl", VariantType.Supported), ("b.stl", VariantType.Presupported));

        var res = await _client.GetAsync($"/api/v1/models/{id}/download.zip?variantType=presupported");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var entries = await ReadZip(res);
        Assert.Equal(new[] { "b.stl" }, entries.Keys.ToArray());
    }

    [Fact]
    public async Task Zip_lists_missing_and_refuses_traversal()
    {
        var dir = MakeDir("missing");
        await File.WriteAllTextAsync(Path.Combine(dir, "ok.stl"), "ok");
        await File.WriteAllTextAsync(Path.Combine(_factory.Root, "zip-secret.stl"), "secret");
        var id = await Seed(dir,
            ("ok.stl", VariantType.Unsupported),
            ("gone.stl", VariantType.Unsupported),
            ("../../../../zip-secret.stl", VariantType.Unsupported));

        var res = await _client.GetAsync($"/api/v1/models/{id}/download.zip");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var entries = await ReadZip(res);
        Assert.Equal("ok", entries["ok.stl"]);
        Assert.DoesNotContain(entries.Values, v => v == "secret");
        Assert.Contains("gone.stl", entries["_MISSING_FILES.txt"]);
        Assert.Contains("refused", entries["_MISSING_FILES.txt"]);
    }

    [Fact]
    public async Task Zip_errors()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/models/{Guid.NewGuid()}/download.zip")).StatusCode);

        var dir = MakeDir("errors");
        var id = await Seed(dir, ("nothere.stl", VariantType.Supported));
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/api/v1/models/{id}/download.zip?variantType=bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/api/v1/models/{id}/download.zip?variantType=42")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/models/{id}/download.zip?variantType=gcode")).StatusCode);
        // only file is missing on disk
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/models/{id}/download.zip")).StatusCode);

        var outside = Path.Combine(Path.GetTempPath(), $"fk-zip-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "x.stl"), "x");
        var outId = await Seed(outside, ("x.stl", VariantType.Supported));
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync($"/api/v1/models/{outId}/download.zip")).StatusCode);
    }

    [Theory]
    [InlineData("Maker", "Dragon", null, "Maker - Dragon.zip")]
    [InlineData(null, "Dragon", VariantType.Supported, "Dragon (Supported).zip")]
    [InlineData("A/B", "C:D?\"", null, "A_B - C_D__.zip")]
    [InlineData("", "...", null, "model.zip")]
    public void Zip_file_name_is_sanitised(string? creator, string model, VariantType? type, string expected)
        => Assert.Equal(expected, ModelZipEndpoints.ZipFileName(creator, model, type));

    [Fact]
    public void Unique_names_get_suffixes()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("a/b.stl", ModelZipEndpoints.UniqueName("a/b.stl", used));
        Assert.Equal("A/B (2).stl", ModelZipEndpoints.UniqueName("A/B.stl", used));
        Assert.Equal("a/b (3).stl", ModelZipEndpoints.UniqueName("a/b.stl", used));
    }
}
