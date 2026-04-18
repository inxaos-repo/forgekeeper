namespace Forgekeeper.Core.Models;

/// <summary>
/// Represents the parsed contents of a plugin's manifest.json file.
/// Deserialized with snake_case JSON naming policy.
/// </summary>
public class PluginManifest
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string SdkVersion { get; set; } = "";
    public string MinSdkVersion { get; set; } = "";
    public string MaxSdkVersion { get; set; } = "";
    public string MinForgekeeperVersion { get; set; } = "";
    public string Author { get; set; } = "";
    public string? Email { get; set; }
    public string Description { get; set; } = "";
    public string? Homepage { get; set; }
    public string? SourceUrl { get; set; }
    public string? License { get; set; }
    public List<string> Tags { get; set; } = [];
    public string EntryAssembly { get; set; } = "";
    public string? Icon { get; set; }
}
