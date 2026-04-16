namespace Forgekeeper.Core.Models;

/// <summary>
/// Stores per-plugin configuration key/value pairs in the database.
/// Sensitive values (secrets, tokens) are marked with IsEncrypted.
/// </summary>
public class PluginConfig
{
    public Guid Id { get; set; }

    /// <summary>Plugin slug (e.g., "mmf", "thangs").</summary>
    public string PluginSlug { get; set; } = string.Empty;

    /// <summary>Config key (e.g., "CLIENT_ID", "ACCESS_TOKEN").</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Config value (plaintext or encrypted depending on IsEncrypted).</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Whether this value should be treated as sensitive (masked in API responses).</summary>
    public bool IsEncrypted { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
