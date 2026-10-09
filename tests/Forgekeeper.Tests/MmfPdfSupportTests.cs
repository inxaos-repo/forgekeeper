using System.Text.Json;
using Forgekeeper.Core.Enums;
using Forgekeeper.Infrastructure.Services;
using Forgekeeper.PluginSdk;
using Forgekeeper.Scraper.Mmf;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>PDF rulebooks / faction books from MMF (2026-10-09). Fixtures redacted from live /api/v2/objects/{id}.</summary>
public class MmfPdfSupportTests
{
    private static JsonDocument Load(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mmf", name)));

    [Fact]
    public void PdfFileItem_IsDownloadedAsIs_NotArchive()
    {
        using var doc = Load("object-pdf-file-item.redacted.json");
        var t = Assert.Single(MmfScraperPlugin.ResolveDownloadTargets(doc.RootElement, "Faction Book"));
        Assert.EndsWith(".pdf", t.FileName);
        Assert.False(t.IsArchive);
        Assert.StartsWith("https://www.myminifactory.com/download/823971", t.Url);
    }

    [Fact]
    public void PdfOnlyObject_WithNoFiles_IsDetected()
    {
        using var doc = Load("object-pdf-only.redacted.json");
        Assert.Empty(MmfScraperPlugin.ResolveDownloadTargets(doc.RootElement, "Core Rules"));
        Assert.True(MmfScraperPlugin.IsPdfOnlyObject(doc.RootElement));
        using var other = Load("object-files-only.redacted.json");
        Assert.False(MmfScraperPlugin.IsPdfOnlyObject(other.RootElement));
    }

    [Theory]
    [InlineData("Rules.pdf", "document")]
    [InlineData("Model.zip", "archive")]
    [InlineData("part.stl", "model")]
    [InlineData("cover.jpg", "image")]
    [InlineData("readme.xyz", "other")]
    public void ClassifyFileKind_Works(string name, string kind) =>
        Assert.Equal(kind, MmfScraperPlugin.ClassifyFileKind(name));

    private static string Tmp(byte[] content)
    {
        var p = Path.Combine(Path.GetTempPath(), $"fkpdf-{Guid.NewGuid():N}");
        File.WriteAllBytes(p, content);
        return p;
    }

    [Fact]
    public void VerifyMagic_AcceptsRealPdf()
    {
        var p = Tmp("%PDF-1.7\n%redacted"u8.ToArray());
        try { MmfDownloader.VerifyMagic(p, "Rules.pdf"); } finally { File.Delete(p); }
    }

    [Fact]
    public void VerifyMagic_RejectsHtmlSavedAsPdf()
    {
        var p = Tmp("<!DOCTYPE html><title>Just a moment...</title>"u8.ToArray());
        try { Assert.Throws<InvalidDataException>(() => MmfDownloader.VerifyMagic(p, "Rules.pdf")); }
        finally { File.Delete(p); }
    }

    [Fact]
    public void VerifyMagic_IgnoresUnknownExtensions()
    {
        var p = Tmp("anything"u8.ToArray());
        try { MmfDownloader.VerifyMagic(p, "notes.xyz"); } finally { File.Delete(p); }
    }

    [Fact]
    public void Metadata_RecordsPdfAsDocument()
    {
        var model = new ScrapedModel { ExternalId = "823971", Name = "Faction Book", CreatorName = "Creator" };
        var files = new List<DownloadedFile>
        {
            new() { Filename = "Faction Book.pdf", LocalPath = "/x/Creator/Faction Book/Faction Book.pdf", Size = 10, Variant = "document" },
        };
        var md = MmfScraperPlugin.BuildMetadata(model, null, files);
        var f = Assert.Single((List<Dictionary<string, object?>>)md["files"]!);
        Assert.Equal("document", f["kind"]);
        Assert.Equal(new List<string> { "Faction Book.pdf" }, md["documents"]);
    }

    [Fact]
    public void Scanner_MapsPdf()
    {
        Assert.Equal(FileType.Pdf, FileScannerService.DetectFileType(".pdf"));
    }
}
