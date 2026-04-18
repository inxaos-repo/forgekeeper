using System.Text.Json.Serialization;

namespace Forgekeeper.Core.Models;

/// <summary>
/// Represents the top-level plugin registry.json from the community registry.
/// </summary>
public class PluginRegistry
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "1";

    [JsonPropertyName("updated")]
    public DateTime Updated { get; set; }

    [JsonPropertyName("plugins")]
    public List<PluginRegistryEntry> Plugins { get; set; } = [];
}

/// <summary>
/// A single plugin entry in registry.json.
/// </summary>
public class PluginRegistryEntry
{
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("author_url")]
    public string? AuthorUrl { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("homepage")]
    public string? Homepage { get; set; }

    [JsonPropertyName("source_url")]
    public string? SourceUrl { get; set; }

    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; set; } = "";

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("sdk_version")]
    public string SdkVersion { get; set; } = "";

    [JsonPropertyName("min_sdk_version")]
    public string MinSdkVersion { get; set; } = "";

    [JsonPropertyName("max_sdk_version")]
    public string? MaxSdkVersion { get; set; }

    [JsonPropertyName("min_forgekeeper_version")]
    public string MinForgekeeperVersion { get; set; } = "";

    [JsonPropertyName("checksum_sha256")]
    public string? ChecksumSha256 { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("license")]
    public string? License { get; set; }

    [JsonPropertyName("updated")]
    public DateTime Updated { get; set; }

    [JsonPropertyName("downloads")]
    public int Downloads { get; set; }
}

/// <summary>
/// Information about an available plugin update.
/// </summary>
public class PluginUpdateInfo
{
    public string Slug { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string AvailableVersion { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public bool IsCompatible { get; set; }
}
