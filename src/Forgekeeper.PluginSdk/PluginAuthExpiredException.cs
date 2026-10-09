namespace Forgekeeper.PluginSdk;

/// <summary>
/// Thrown by a plugin (e.g. from FetchManifestAsync) when its saved session/token was
/// rejected. The host pauses the sync, checkpoints, and surfaces "reconnect needed"
/// instead of failing the run.
/// </summary>
public sealed class PluginAuthExpiredException(string message) : Exception(message);
