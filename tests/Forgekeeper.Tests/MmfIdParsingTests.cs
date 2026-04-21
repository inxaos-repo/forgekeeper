using Forgekeeper.Scraper.Mmf;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Tests for MmfScraperPlugin.SplitManifestId — verifies that manifest IDs
/// with various prefixes (object-, bundle-, collection-) or bare numeric IDs
/// are correctly parsed into (numericId, type) tuples.
/// </summary>
public class MmfIdParsingTests
{
    [Theory]
    [InlineData("object-786967", "786967", "object")]
    [InlineData("bundle-2447", "2447", "bundle")]
    [InlineData("collection-999", "999", "collection")]
    [InlineData("OBJECT-42", "42", "object")]   // prefix normalized to lower
    [InlineData("123456", "123456", "object")]   // bare numeric → object
    public void SplitManifestId_ParsesKnownShapes(string input, string expectedNumeric, string expectedType)
    {
        var (numericId, type) = MmfScraperPlugin.SplitManifestId(input);
        Assert.Equal(expectedNumeric, numericId);
        Assert.Equal(expectedType, type);
    }

    [Fact]
    public void SplitManifestId_Null_ReturnsNullNull()
    {
        var (n, t) = MmfScraperPlugin.SplitManifestId(null);
        Assert.Null(n);
        Assert.Null(t);
    }

    [Fact]
    public void SplitManifestId_Empty_ReturnsNullNull()
    {
        var (n, t) = MmfScraperPlugin.SplitManifestId("");
        Assert.Null(n);
        Assert.Null(t);
    }
}
