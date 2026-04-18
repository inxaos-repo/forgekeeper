using System.Text.Json;
using Forgekeeper.Scraper.Mmf;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Phase 2 tests: background unzip worker, variant detection, archive handling,
/// filename sanitization, JSON parsing robustness, CDN detection, file matching.
/// </summary>
public class Phase2Tests : IDisposable
{
    private readonly string _tempDir;

    public Phase2Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"forgekeeper-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // --- Variant Detection ---

    [Theory]
    [InlineData("model_supported.stl", "supported")]
    [InlineData("Dragon_Unsupported.stl", "unsupported")]
    [InlineData("Base_presupported.stl", "presupported")]
    [InlineData("Model_pre-supported.obj", "presupported")]
    [InlineData("Model_pre_supported.obj", "presupported")]
    [InlineData("scene.lys", "lychee")]
    [InlineData("print.ctb", "chitubox")]
    [InlineData("plain_model.stl", null)]
    [InlineData("no_support_version.stl", "unsupported")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void DetectVariant_CorrectlyIdentifiesVariants(string? filename, string? expected)
    {
        var result = MmfScraperPlugin.DetectVariant(filename);
        Assert.Equal(expected, result);
    }

    // --- Archive Detection ---

    [Theory]
    [InlineData("model.zip", true)]
    [InlineData("model.rar", true)]
    [InlineData("model.7z", true)]
    [InlineData("model.tar", true)]
    [InlineData("model.gz", true)]
    [InlineData("model.stl", false)]
    [InlineData("model.obj", false)]
    [InlineData("model.3mf", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsArchiveFile_CorrectlyIdentifiesArchives(string? filename, bool expected)
    {
        Assert.Equal(expected, MmfScraperPlugin.IsArchiveFile(filename));
    }

    // --- Filename Sanitization ---

    [Theory]
    [InlineData("normal_file.stl", "normal_file.stl")]
    [InlineData("file with spaces.stl", "file with spaces.stl")]
    [InlineData("file/with/slashes.stl", "file_with_slashes.stl")]
    // Note: colons and brackets are valid on Linux, only invalid on Windows
    [InlineData("file:with:colons.stl", "file:with:colons.stl")]
    [InlineData("file<with>brackets.stl", "file<with>brackets.stl")]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    public void SanitizeFilename_RemovesInvalidChars(string? input, string expected)
    {
        Assert.Equal(expected, MmfScraperPlugin.SanitizeFilename(input));
    }

    // --- CDN URL Detection ---

    [Theory]
    [InlineData("https://cdn.myminifactory.com/files/model.zip", true)]
    [InlineData("https://dl.myminifactory.com/files/model.zip", true)]
    [InlineData("https://dl4.myminifactory.com/object-assets/abc/files.zip", true)]
    [InlineData("https://www.myminifactory.com/download/12345", false)]
    [InlineData("https://example.com/file.zip", false)]
    public void IsCdnUrl_DetectsCdnUrls(string url, bool expected)
    {
        Assert.Equal(expected, MmfScraperPlugin.IsCdnUrl(url));
    }

    // --- File Existence Matching ---

    [Fact]
    public void FindExistingFile_ExactMatch()
    {
        var filePath = Path.Combine(_tempDir, "model.stl");
        File.WriteAllBytes(filePath, new byte[1024]);

        var result = MmfScraperPlugin.FindExistingFile(_tempDir, "model.stl", 1024);
        Assert.NotNull(result);
        Assert.Equal(filePath, result);
    }

    [Fact]
    public void FindExistingFile_SizeZero_TrustsExistence()
    {
        var filePath = Path.Combine(_tempDir, "model.stl");
        File.WriteAllBytes(filePath, new byte[500]);

        // Size 0 = unknown, should trust the file exists
        var result = MmfScraperPlugin.FindExistingFile(_tempDir, "model.stl", 0);
        Assert.NotNull(result);
    }

    [Fact]
    public void FindExistingFile_SizeMismatch_ReturnsNull()
    {
        var filePath = Path.Combine(_tempDir, "model.stl");
        File.WriteAllBytes(filePath, new byte[500]);

        var result = MmfScraperPlugin.FindExistingFile(_tempDir, "model.stl", 9999);
        Assert.Null(result);
    }

    [Fact]
    public void FindExistingFile_SubdirectorySearch()
    {
        var subDir = Path.Combine(_tempDir, "supported");
        Directory.CreateDirectory(subDir);
        var filePath = Path.Combine(subDir, "model.stl");
        File.WriteAllBytes(filePath, new byte[1024]);

        var result = MmfScraperPlugin.FindExistingFile(_tempDir, "model.stl", 1024);
        Assert.NotNull(result);
        Assert.Equal(filePath, result);
    }

    [Fact]
    public void FindExistingFile_NoMatch_ReturnsNull()
    {
        var result = MmfScraperPlugin.FindExistingFile(_tempDir, "nonexistent.stl", 0);
        Assert.Null(result);
    }

    // --- JSON Parsing Robustness ---

    [Fact]
    public void ParseModelDetails_HandlesMinimalResponse()
    {
        var json = @"{""id"": 12345, ""name"": ""Test Model""}";
        using var doc = JsonDocument.Parse(json);
        var details = MmfScraperPlugin.ParseModelDetails(doc.RootElement);

        Assert.Equal(12345, details.Id);
        Assert.Equal("Test Model", details.Name);
        Assert.Null(details.Description);
        Assert.Null(details.Designer);
        Assert.Null(details.Tags);
        Assert.Null(details.Images);
    }

    [Fact]
    public void ParseModelDetails_HandlesStringTags()
    {
        var json = @"{""id"": 1, ""name"": ""Test"", ""tags"": [""fantasy"", ""knight"", ""dragon""]}";
        using var doc = JsonDocument.Parse(json);
        var details = MmfScraperPlugin.ParseModelDetails(doc.RootElement);

        Assert.NotNull(details.Tags);
        Assert.Equal(3, details.Tags.Count);
        Assert.Equal("fantasy", details.Tags[0].Name);
    }

    [Fact]
    public void ParseModelDetails_HandlesObjectTags()
    {
        var json = @"{""id"": 1, ""name"": ""Test"", ""tags"": [{""name"": ""fantasy""}, {""name"": ""sci-fi""}]}";
        using var doc = JsonDocument.Parse(json);
        var details = MmfScraperPlugin.ParseModelDetails(doc.RootElement);

        Assert.NotNull(details.Tags);
        Assert.Equal(2, details.Tags.Count);
        Assert.Equal("fantasy", details.Tags[0].Name);
    }

    [Fact]
    public void ParseModelDetails_HandlesNestedImageObjects()
    {
        var json = @"
        {
            ""id"": 1, ""name"": ""Test"",
            ""images"": [{
                ""url"": ""https://example.com/thumb.jpg"",
                ""original"": {""url"": ""https://example.com/full.jpg"", ""width"": 1920, ""height"": 1080},
                ""standard"": {""url"": ""https://example.com/std.jpg""}
            }]
        }
        ";
        using var doc = JsonDocument.Parse(json);
        var details = MmfScraperPlugin.ParseModelDetails(doc.RootElement);

        Assert.NotNull(details.Images);
        Assert.Single(details.Images);
        Assert.Equal("https://example.com/thumb.jpg", details.Images[0].Url);
        Assert.Equal("https://example.com/full.jpg", details.Images[0].Original?.Url);
    }

    [Fact]
    public void ParseModelDetails_HandlesDesigner()
    {
        var json = @"{""id"": 1, ""name"": ""Test"", ""designer"": {""id"": 42, ""name"": ""Epic Studio"", ""username"": ""epicstudio""}}";
        using var doc = JsonDocument.Parse(json);
        var details = MmfScraperPlugin.ParseModelDetails(doc.RootElement);

        Assert.NotNull(details.Designer);
        Assert.Equal(42, details.Designer.Id);
        Assert.Equal("Epic Studio", details.Designer.Name);
        Assert.Equal("epicstudio", details.Designer.Username);
    }

    [Fact]
    public void ParseModelDetails_HandlesDates()
    {
        var json = @"{""id"": 1, ""name"": ""Test"", ""created_at"": ""2024-06-15T10:30:00Z"", ""updated_at"": ""2025-01-20T14:00:00Z""}";
        using var doc = JsonDocument.Parse(json);
        var details = MmfScraperPlugin.ParseModelDetails(doc.RootElement);

        Assert.NotNull(details.CreatedAt);
        Assert.NotNull(details.UpdatedAt);
    }

    [Fact]
    public void ParseModelDetails_HandlesNullDates()
    {
        var json = @"{""id"": 1, ""name"": ""Test"", ""created_at"": null}";
        using var doc = JsonDocument.Parse(json);
        var details = MmfScraperPlugin.ParseModelDetails(doc.RootElement);

        Assert.Null(details.CreatedAt);
    }

    // --- File Parsing ---

    [Fact]
    public void ParseFiles_HandlesNullSize()
    {
        var json = @"[{""id"": 1, ""filename"": ""model.stl"", ""size"": null, ""download_url"": ""https://example.com/dl""}]";
        using var doc = JsonDocument.Parse(json);
        var files = MmfScraperPlugin.ParseFiles(doc.RootElement);

        Assert.Single(files);
        Assert.Equal(0, files[0].Size); // null → 0
        Assert.Equal("https://example.com/dl", files[0].DownloadUrl);
    }

    [Fact]
    public void ParseFiles_HandlesSizeAsString()
    {
        var json = @"[{""id"": 1, ""filename"": ""model.stl"", ""size"": ""1048576"", ""download_url"": ""https://example.com/dl""}]";
        using var doc = JsonDocument.Parse(json);
        var files = MmfScraperPlugin.ParseFiles(doc.RootElement);

        Assert.Equal(1048576, files[0].Size);
    }

    [Fact]
    public void ParseFiles_HandlesSizeAsNumber()
    {
        var json = @"[{""id"": 1, ""filename"": ""model.stl"", ""size"": 2097152, ""download_url"": ""https://example.com/dl""}]";
        using var doc = JsonDocument.Parse(json);
        var files = MmfScraperPlugin.ParseFiles(doc.RootElement);

        Assert.Equal(2097152, files[0].Size);
    }

    [Fact]
    public void ParseFiles_HandlesEmptyArray()
    {
        var json = "[]";
        using var doc = JsonDocument.Parse(json);
        var files = MmfScraperPlugin.ParseFiles(doc.RootElement);

        Assert.Empty(files);
    }

    [Fact]
    public void ParseFiles_HandlesMultipleFiles()
    {
        var json = @"[
            {""id"": 1, ""filename"": ""body.stl"", ""size"": 1000, ""download_url"": ""https://example.com/1""},
            {""id"": 2, ""filename"": ""base.stl"", ""size"": 500, ""download_url"": ""https://example.com/2""},
            {""id"": 3, ""filename"": ""arms.stl"", ""size"": null, ""download_url"": null}
        ]";
        using var doc = JsonDocument.Parse(json);
        var files = MmfScraperPlugin.ParseFiles(doc.RootElement);

        Assert.Equal(3, files.Count);
        Assert.Equal("body.stl", files[0].Filename);
        Assert.Null(files[2].DownloadUrl);
    }

    // --- Old Version Cleanup ---

    [Fact]
    public void CleanupOldVersions_RemovesOlderVersion()
    {
        var parent = Path.Combine(_tempDir, "creator", "model");
        var v1 = Path.Combine(parent, "Model v1");
        var v2 = Path.Combine(parent, "Model v2");
        Directory.CreateDirectory(v1);
        Directory.CreateDirectory(v2);
        File.WriteAllText(Path.Combine(v1, "old.stl"), "old");
        File.WriteAllText(Path.Combine(v2, "new.stl"), "new");

        var mockLogger = new Mock<ILogger>();
        MmfScraperPlugin.CleanupOldVersions(v2, mockLogger.Object);

        Assert.False(Directory.Exists(v1));
        Assert.True(Directory.Exists(v2));
    }

    [Fact]
    public void CleanupOldVersions_IgnoresUnversionedDirs()
    {
        var parent = Path.Combine(_tempDir, "creator", "model");
        var other = Path.Combine(parent, "OtherModel");
        var current = Path.Combine(parent, "Model v2");
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(other, "test.stl"), "test");

        var mockLogger = new Mock<ILogger>();
        MmfScraperPlugin.CleanupOldVersions(current, mockLogger.Object);

        Assert.True(Directory.Exists(other)); // Should not be touched
    }
}
