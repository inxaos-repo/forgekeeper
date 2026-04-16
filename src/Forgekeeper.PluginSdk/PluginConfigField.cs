namespace Forgekeeper.PluginSdk;

/// <summary>
/// Describes a configuration field that a plugin requires.
/// Used to generate admin UI and validate config values.
/// </summary>
public class PluginConfigField
{
    /// <summary>Config key (e.g., "CLIENT_ID").</summary>
    public required string Key { get; init; }

    /// <summary>Human-readable label for the UI.</summary>
    public required string Label { get; init; }

    /// <summary>Field type: string, secret, url, number, bool.</summary>
    public required PluginConfigFieldType Type { get; init; }

    /// <summary>Default value if not configured.</summary>
    public string? DefaultValue { get; init; }

    /// <summary>Whether this field must be provided.</summary>
    public bool Required { get; init; }

    /// <summary>Help text shown in the UI.</summary>
    public string? HelpText { get; init; }
}

public enum PluginConfigFieldType
{
    String,
    Secret,
    Url,
    Number,
    Bool
}
