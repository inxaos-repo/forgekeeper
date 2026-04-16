namespace Forgekeeper.PluginSdk;

/// <summary>
/// Result of an authentication attempt.
/// </summary>
public class AuthResult
{
    /// <summary>Whether the plugin is now authenticated and ready to scrape.</summary>
    public bool Authenticated { get; init; }

    /// <summary>
    /// If browser auth is needed, the URL the user should visit.
    /// The host will redirect the user here and handle the callback.
    /// </summary>
    public string? AuthUrl { get; init; }

    /// <summary>Human-readable status message.</summary>
    public string? Message { get; init; }

    public static AuthResult Success(string? message = null) =>
        new() { Authenticated = true, Message = message ?? "Authenticated successfully" };

    public static AuthResult NeedsBrowser(string authUrl, string? message = null) =>
        new() { Authenticated = false, AuthUrl = authUrl, Message = message ?? "Browser authentication required" };

    public static AuthResult Failed(string message) =>
        new() { Authenticated = false, Message = message };
}
