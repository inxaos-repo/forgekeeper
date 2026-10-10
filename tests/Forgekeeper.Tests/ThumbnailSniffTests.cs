using System.Text;
using Forgekeeper.Infrastructure.Services;
using Xunit;

namespace Forgekeeper.Tests;

public class ThumbnailSniffTests
{
    [Fact]
    public void ZipNamedStl_IsRejected() =>
        Assert.Equal("zip archive", ThumbnailService.SniffNonMesh(new byte[] { 0x50, 0x4B, 3, 4, 20, 0, 0, 0 }));

    [Theory]
    [InlineData("<!DOCTYPE html><html>")]
    [InlineData("  <html lang=\"en\">")]
    public void HtmlNamedStl_IsRejected(string head) =>
        Assert.Equal("html page", ThumbnailService.SniffNonMesh(Encoding.ASCII.GetBytes(head)));

    [Theory]
    [InlineData("solid cube\n facet normal 0 0 1")]
    [InlineData("binary stl header from some exporter....")]
    public void RealMeshHeaders_AreAccepted(string head) =>
        Assert.Null(ThumbnailService.SniffNonMesh(Encoding.ASCII.GetBytes(head)));
}
