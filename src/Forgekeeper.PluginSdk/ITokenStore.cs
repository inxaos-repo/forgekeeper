namespace Forgekeeper.PluginSdk;

/// <summary>
/// Persistent token storage for plugins.
/// Tokens are scoped per-plugin and stored encrypted in the database.
/// </summary>
public interface ITokenStore
{
    /// <summary>Get a stored token by key.</summary>
    Task<string?> GetTokenAsync(string key, CancellationToken ct = default);

    /// <summary>Save a token. Overwrites any existing value for this key.</summary>
    Task SaveTokenAsync(string key, string value, CancellationToken ct = default);

    /// <summary>Delete a stored token.</summary>
    Task DeleteTokenAsync(string key, CancellationToken ct = default);
}
