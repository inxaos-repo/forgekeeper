using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forgekeeper.Core.Models;

/// <summary>
/// Represents the metadata.json integration contract — the "ID3 tag" for 3D model directories.
/// Written by external scrapers/downloaders AND by Forgekeeper for manual imports.
/// Forgekeeper does NOT overwrite scraper-provided metadata.json files.
/// </summary>
public class SourceMetadata
{
    [JsonPropertyName("metadataVersion")]
    public int MetadataVersion { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("externalId")]
    public string ExternalId { get; set; } = string.Empty;

    [JsonPropertyName("externalUrl")]
    public string? ExternalUrl { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("creator")]
    public MetadataCreator? Creator { get; set; }

    [JsonPropertyName("dates")]
    public MetadataDates? Dates { get; set; }

    [JsonPropertyName("acquisition")]
    public MetadataAcquisition? Acquisition { get; set; }

    [JsonPropertyName("images")]
    public List<MetadataImage>? Images { get; set; }

    [JsonPropertyName("files")]
    public List<MetadataFile>? Files { get; set; }

    [JsonPropertyName("extra")]
    public Dictionary<string, object>? Extra { get; set; }

    // --- ID3-inspired fields ---

    [JsonPropertyName("license")]
    public LicenseInfo? License { get; set; }

    [JsonPropertyName("collection")]
    public CollectionInfo? Collection { get; set; }

    [JsonPropertyName("sourceRating")]
    public SourceRatingInfo? Rating { get; set; }

    [JsonPropertyName("relatedModels")]
    public List<RelatedModelInfo>? RelatedModels { get; set; }

    [JsonPropertyName("printSettings")]
    public PrintSettingsInfo? PrintSettings { get; set; }

    [JsonPropertyName("components")]
    public List<ComponentInfo>? Components { get; set; }

    [JsonPropertyName("fileHashes")]
    public Dictionary<string, string>? FileHashes { get; set; } // localPath -> "sha256:hash"

    /// <summary>
    /// Preserves unknown/future JSON fields during deserialization so they survive round-trips.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class MetadataCreator
{
    [JsonPropertyName("externalId")]
    public string? ExternalId { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("profileUrl")]
    public string? ProfileUrl { get; set; }
}

public class MetadataDates
{
    [JsonPropertyName("created")]
    public DateTime? Created { get; set; }

    [JsonPropertyName("updated")]
    public DateTime? Updated { get; set; }

    [JsonPropertyName("published")]
    public DateTime? Published { get; set; }

    [JsonPropertyName("addedToLibrary")]
    public DateTime? AddedToLibrary { get; set; }

    [JsonPropertyName("downloaded")]
    public DateTime? Downloaded { get; set; }
}

public class MetadataAcquisition
{
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("orderId")]
    public string? OrderId { get; set; }

    [JsonPropertyName("campaignId")]
    public string? CampaignId { get; set; }
}

public class MetadataImage
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("localPath")]
    public string? LocalPath { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public class MetadataFile
{
    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("localPath")]
    public string? LocalPath { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("variant")]
    public string? Variant { get; set; }

    [JsonPropertyName("downloadedAt")]
    public DateTime? DownloadedAt { get; set; }

    [JsonPropertyName("originalFilename")]
    public string? OriginalFilename { get; set; }
}

// --- New ID3-inspired types ---

public class LicenseInfo
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "unknown"; // personal, commercial, cc-by, cc-by-nc, cc-by-sa, cc0, unknown

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public class CollectionInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("externalId")]
    public string? ExternalId { get; set; }

    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("total")]
    public int? Total { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public class SourceRatingInfo
{
    [JsonPropertyName("score")]
    public double? Score { get; set; }

    [JsonPropertyName("maxScore")]
    public double? MaxScore { get; set; }

    [JsonPropertyName("votes")]
    public int? Votes { get; set; }

    [JsonPropertyName("downloads")]
    public int? Downloads { get; set; }

    [JsonPropertyName("likes")]
    public int? Likes { get; set; }
}

public class RelatedModelInfo
{
    [JsonPropertyName("externalId")]
    public string ExternalId { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("relation")]
    public string Relation { get; set; } = "collection"; // companion, collection, remix, alternate, base
}

public class PrintSettingsInfo
{
    [JsonPropertyName("technology")]
    public string? Technology { get; set; } // resin, fdm, sla, msla

    [JsonPropertyName("layerHeight")]
    public double? LayerHeight { get; set; }

    [JsonPropertyName("scale")]
    public string? Scale { get; set; }

    [JsonPropertyName("supportsRequired")]
    public bool? SupportsRequired { get; set; }

    [JsonPropertyName("estimatedPrintTime")]
    public string? EstimatedPrintTime { get; set; }

    [JsonPropertyName("estimatedMaterial")]
    public string? EstimatedMaterial { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

public class PhysicalProperties
{
    [JsonPropertyName("boundingBox")]
    public BoundingBox? BoundingBox { get; set; }

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "mm";

    [JsonPropertyName("triangleCount")]
    public int? TriangleCount { get; set; }

    [JsonPropertyName("isWatertight")]
    public bool? IsWatertight { get; set; }

    [JsonPropertyName("volume")]
    public double? Volume { get; set; }
}

public class BoundingBox
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("z")]
    public double Z { get; set; }
}

public class PrintHistoryEntry
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("printer")]
    public string? Printer { get; set; }

    [JsonPropertyName("technology")]
    public string? Technology { get; set; }

    [JsonPropertyName("material")]
    public string? Material { get; set; }

    [JsonPropertyName("layerHeight")]
    public double? LayerHeight { get; set; }

    [JsonPropertyName("scale")]
    public double? Scale { get; set; }

    [JsonPropertyName("result")]
    public string Result { get; set; } = "success"; // success, failed, partial

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("duration")]
    public string? Duration { get; set; }

    [JsonPropertyName("photos")]
    public List<string>? Photos { get; set; }

    [JsonPropertyName("variant")]
    public string? Variant { get; set; }
}

public class ComponentInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;

    [JsonPropertyName("group")]
    public string? Group { get; set; } // weapon, head, base, etc — null = no alternatives
}
