using Xunit;
using System.Text.Json;
using Forgekeeper.Core.Models;

namespace Forgekeeper.Tests;

public class MetadataParsingTests
{
    [Fact]
    public void ParsesFullMetadataJson()
    {
        var json = """
        {
          "metadataVersion": 1,
          "source": "mmf",
          "externalId": "123456",
          "externalUrl": "https://www.myminifactory.com/object/123456",
          "name": "Space Marine Captain with Power Sword",
          "description": "A highly detailed miniature",
          "type": "object",
          "tags": ["warhammer", "40k", "space marine"],
          "creator": {
            "externalId": "789",
            "username": "AwesomeSculptor",
            "displayName": "Awesome Sculptor Studio",
            "avatarUrl": "https://example.com/avatar.jpg",
            "profileUrl": "https://example.com/profile"
          },
          "dates": {
            "created": "2025-06-15T10:30:00+00:00",
            "updated": "2025-08-20T14:00:00+00:00",
            "downloaded": "2026-04-15T18:00:00+00:00"
          },
          "acquisition": {
            "method": "purchase",
            "orderId": "F51015845C"
          },
          "extra": {
            "mmf_bundle_id": 3147
          }
        }
        """;

        var metadata = JsonSerializer.Deserialize<SourceMetadata>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(metadata);
        Assert.Equal(1, metadata.MetadataVersion);
        Assert.Equal("mmf", metadata.Source);
        Assert.Equal("123456", metadata.ExternalId);
        Assert.Equal("Space Marine Captain with Power Sword", metadata.Name);
        Assert.Equal(3, metadata.Tags!.Count);
        Assert.Contains("warhammer", metadata.Tags);
        Assert.Equal("Awesome Sculptor Studio", metadata.Creator!.DisplayName);
        Assert.NotNull(metadata.Dates?.Created);
        Assert.NotNull(metadata.Dates?.Downloaded);
        Assert.Equal("purchase", metadata.Acquisition?.Method);
        Assert.NotNull(metadata.Extra);
    }

    [Fact]
    public void ParsesMinimalMetadataJson()
    {
        var json = """
        {
          "metadataVersion": 1,
          "source": "thangs",
          "externalId": "abc-123",
          "name": "Dragon Miniature",
          "dates": { "downloaded": "2026-04-15T18:00:00Z" }
        }
        """;

        var metadata = JsonSerializer.Deserialize<SourceMetadata>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(metadata);
        Assert.Equal(1, metadata.MetadataVersion);
        Assert.Equal("thangs", metadata.Source);
        Assert.Equal("Dragon Miniature", metadata.Name);
        Assert.Null(metadata.Creator);
        Assert.Null(metadata.Tags);
        Assert.Null(metadata.Extra);
    }
}
