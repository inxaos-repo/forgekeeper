using Forgekeeper.PluginSdk;
using Forgekeeper.Scraper.Mmf;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Schema-regression tests for <see cref="MmfScraperPlugin"/>.
///
/// These tests lock the plugin's config-field contract so that:
///
///   (a) Sensitive fields keep their <c>Secret</c> type. If someone ever
///       changes <c>MMF_PASSWORD</c> or <c>CLIENT_SECRET</c> back to
///       <c>String</c>, encryption-at-rest silently breaks and credentials
///       end up in plaintext in the DB. Test catches that immediately.
///
///   (b) The <c>VERBOSE_LOGGING</c> toggle (added in the verbose-gating PR)
///       keeps its default "false". If it flips to "true" by accident, every
///       production sync becomes noisy and the password fingerprint log line
///       returns. Test catches that immediately.
///
///   (c) Required fields stay required. If someone makes <c>MMF_USERNAME</c>
///       optional, the plugin can load with no auth and silently fail later
///       (harder-to-diagnose than a clear startup error).
///
/// These are cheap, fast, and pinpoint exactly what changed when they fail.
/// Not a substitute for end-to-end tests, but the first line of defense.
/// </summary>
public class MmfScraperSchemaRegressionTests
{
    private static readonly MmfScraperPlugin Plugin = new();

    [Fact]
    public void Plugin_Identity_IsStable()
    {
        Assert.Equal("mmf", Plugin.SourceSlug);
        Assert.Equal("MyMiniFactory", Plugin.SourceName);
        Assert.False(string.IsNullOrWhiteSpace(Plugin.Version));
    }

    [Fact]
    public void MmfPassword_Field_IsSecretType()
    {
        var field = Plugin.ConfigSchema.FirstOrDefault(f => f.Key == "MMF_PASSWORD");
        Assert.NotNull(field);
        Assert.Equal(PluginConfigFieldType.Secret, field!.Type);
        Assert.True(field.Required,
            "MMF_PASSWORD must stay Required — making it optional allows the plugin to load with no password and fail silently at sync time.");
    }

    [Fact]
    public void MmfUsername_Field_IsRequiredString()
    {
        var field = Plugin.ConfigSchema.FirstOrDefault(f => f.Key == "MMF_USERNAME");
        Assert.NotNull(field);
        Assert.Equal(PluginConfigFieldType.String, field!.Type);
        Assert.True(field.Required,
            "MMF_USERNAME must stay Required.");
    }

    [Fact]
    public void VerboseLogging_Toggle_ExistsAndDefaultsToFalse()
    {
        // VERBOSE_LOGGING default MUST remain 'false'. A default of 'true' would
        // produce password fingerprints + step-by-step traces on every sync in production.
        var field = Plugin.ConfigSchema.FirstOrDefault(f => f.Key == "VERBOSE_LOGGING");
        Assert.NotNull(field);
        Assert.Equal(PluginConfigFieldType.String, field!.Type);
        Assert.Equal("false", field.DefaultValue);
        Assert.False(field.Required);
    }

    [Fact]
    public void ClientId_Field_HasDownloaderV2Default()
    {
        // CLIENT_ID default is the documented 'downloader_v2' value MMF uses for
        // the open client. Changing it breaks OAuth for new installs.
        var field = Plugin.ConfigSchema.FirstOrDefault(f => f.Key == "CLIENT_ID");
        Assert.NotNull(field);
        Assert.Equal("downloader_v2", field!.DefaultValue);
    }

    [Fact]
    public void FlareSolverrUrl_Field_ExistsAndIsOptional()
    {
        var field = Plugin.ConfigSchema.FirstOrDefault(f => f.Key == "FLARESOLVERR_URL");
        Assert.NotNull(field);
        Assert.False(field!.Required,
            "FLARESOLVERR_URL is optional — if absent, the plugin skips the CF challenge path entirely.");
    }

    [Fact]
    public void AllSecretFields_AreMarkedSecretType()
    {
        // Any field whose Key hints at a secret (password, token, secret, key)
        // must have Type=Secret so it's encrypted at rest. Keeps future field
        // additions honest — if someone adds MMF_API_KEY as type String, this
        // fails fast.
        var likelySecrets = Plugin.ConfigSchema
            .Where(f =>
                f.Key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) ||
                f.Key.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase) ||
                f.Key.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase) ||
                f.Key.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.All(likelySecrets, f =>
            Assert.True(
                f.Type == PluginConfigFieldType.Secret,
                $"Field '{f.Key}' looks like a secret but has type '{f.Type}'. " +
                $"Change it to Secret so encryption-at-rest applies."));
    }

    [Fact]
    public void ClientSecret_Field_IsOptionalAndSecret()
    {
        // CLIENT_SECRET must be:
        //   (a) present in the schema — without it the OAuth download flow has no config field
        //   (b) Secret type — so it's encrypted at rest (caught by AllSecretFields_AreMarkedSecretType too)
        //   (c) NOT Required — manifest sync must work without an OAuth app registration
        var field = Plugin.ConfigSchema.FirstOrDefault(f => f.Key == "CLIENT_SECRET");
        Assert.NotNull(field);
        Assert.Equal(PluginConfigFieldType.Secret, field!.Type);
        Assert.False(field.Required,
            "CLIENT_SECRET must remain optional so manifest-only mode works without OAuth.");
    }

    [Fact]
    public void ConfigSchema_HasNoDuplicateKeys()
    {
        var keys = Plugin.ConfigSchema.Select(f => f.Key).ToList();
        var dupes = keys
            .GroupBy(k => k)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(dupes);
    }

    [Fact]
    public void ConfigSchema_AllFields_HaveNonEmptyHelpText()
    {
        // Every config field must carry help text. The admin UI relies on it,
        // and omitting it is how a "mystery meat" field ends up shipped.
        var missing = Plugin.ConfigSchema
            .Where(f => string.IsNullOrWhiteSpace(f.HelpText))
            .Select(f => f.Key)
            .ToList();

        Assert.True(missing.Count == 0,
            $"Fields missing HelpText: {string.Join(", ", missing)}");
    }

    [Fact]
    public void RequiresBrowserAuth_IsFalse()
    {
        // The MMF plugin handles its own browser-based login via Playwright
        // internally; it does NOT delegate to the host's browser-auth flow.
        // Flipping this to true would cause the host to pop a browser window
        // the plugin doesn't know about, breaking sync.
        Assert.False(Plugin.RequiresBrowserAuth);
    }
}
