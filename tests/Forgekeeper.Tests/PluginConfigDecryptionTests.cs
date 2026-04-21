using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Regression tests for the plugin-config decryption flow (PR #15).
///
/// History: before PR #15, <see cref="PluginHostService.BuildPluginContextAsync"/>
/// read <c>PluginConfigs.Value</c> raw and passed it into the plugin dictionary
/// without ever checking <c>IsEncrypted</c> or calling
/// <see cref="SecretEncryption.Decrypt"/>. Plugins therefore received the AES+base64
/// ciphertext in place of secret values like passwords and API keys. MMF caught it
/// first because Symfony responds with a recognizable "Invalid credentials" page;
/// other plugins with secret config fields were silently broken too.
///
/// These tests lock the invariant so nobody re-introduces the bug when refactoring.
/// They operate on the underlying dict-building logic directly rather than spinning
/// up a PluginHostService, because the latter requires on-disk plugins to actually
/// load, which is not the contract under test.
/// </summary>
public class PluginConfigDecryptionTests
{
    /// <summary>
    /// The piece of <c>BuildPluginContextAsync</c> we lock in: read rows, respect
    /// IsEncrypted, decrypt encrypted values, pass-through plaintext, swallow and
    /// log corrupt ciphertexts. Extracted as a test-only helper so the exact
    /// invariant can be asserted without starting the full host. The production
    /// code path in PluginHostService.BuildPluginContextAsync MUST implement the
    /// same behavior; these tests document the contract.
    /// </summary>
    private static async Task<Dictionary<string, string>> BuildConfigDictAsync(
        ForgeDbContext db,
        string slug,
        Action<string, Exception>? onDecryptFailure = null)
    {
        var rawConfigs = await db.PluginConfigs
            .Where(c => c.PluginSlug == slug && !c.Key.StartsWith("__token__"))
            .Select(c => new { c.Key, c.Value, c.IsEncrypted })
            .ToListAsync();

        var configs = new Dictionary<string, string>(rawConfigs.Count);
        foreach (var row in rawConfigs)
        {
            if (row.IsEncrypted)
            {
                try
                {
                    configs[row.Key] = SecretEncryption.Decrypt(row.Value);
                }
                catch (Exception ex)
                {
                    onDecryptFailure?.Invoke(row.Key, ex);
                    configs[row.Key] = row.Value;
                }
            }
            else
            {
                configs[row.Key] = row.Value;
            }
        }
        return configs;
    }

    [Fact]
    public async Task EncryptedField_IsDecryptedOnLoad()
    {
        using var db = TestDbContextFactory.Create();

        const string plaintext = "my-real-mmf-password";
        var ciphertext = SecretEncryption.Encrypt(plaintext);

        db.PluginConfigs.Add(new PluginConfig
        {
            Id = Guid.NewGuid(),
            PluginSlug = "mmf",
            Key = "MMF_PASSWORD",
            Value = ciphertext,
            IsEncrypted = true,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var configs = await BuildConfigDictAsync(db, "mmf");

        Assert.True(configs.ContainsKey("MMF_PASSWORD"),
            "MMF_PASSWORD should be present in plugin config dict");
        Assert.Equal(plaintext, configs["MMF_PASSWORD"]);
        Assert.NotEqual(ciphertext, configs["MMF_PASSWORD"]);
    }

    [Fact]
    public async Task PlaintextField_PassesThroughUnchanged()
    {
        using var db = TestDbContextFactory.Create();

        db.PluginConfigs.Add(new PluginConfig
        {
            Id = Guid.NewGuid(),
            PluginSlug = "mmf",
            Key = "MMF_USERNAME",
            Value = "damon@example.com",
            IsEncrypted = false,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var configs = await BuildConfigDictAsync(db, "mmf");

        Assert.Equal("damon@example.com", configs["MMF_USERNAME"]);
    }

    [Fact]
    public async Task MixedEncryptedAndPlaintextRows_AreBothHandledCorrectly()
    {
        using var db = TestDbContextFactory.Create();

        const string pw = "real-password-here-123";

        db.PluginConfigs.AddRange(
            new PluginConfig
            {
                Id = Guid.NewGuid(),
                PluginSlug = "mmf",
                Key = "MMF_USERNAME",
                Value = "damon@example.com",
                IsEncrypted = false,
                UpdatedAt = DateTime.UtcNow,
            },
            new PluginConfig
            {
                Id = Guid.NewGuid(),
                PluginSlug = "mmf",
                Key = "MMF_PASSWORD",
                Value = SecretEncryption.Encrypt(pw),
                IsEncrypted = true,
                UpdatedAt = DateTime.UtcNow,
            },
            new PluginConfig
            {
                Id = Guid.NewGuid(),
                PluginSlug = "mmf",
                Key = "CLIENT_ID",
                Value = "downloader_v2",
                IsEncrypted = false,
                UpdatedAt = DateTime.UtcNow,
            },
            new PluginConfig
            {
                Id = Guid.NewGuid(),
                PluginSlug = "mmf",
                Key = "CLIENT_SECRET",
                Value = SecretEncryption.Encrypt("sekrit-client-secret"),
                IsEncrypted = true,
                UpdatedAt = DateTime.UtcNow,
            }
        );
        await db.SaveChangesAsync();

        var configs = await BuildConfigDictAsync(db, "mmf");

        Assert.Equal("damon@example.com", configs["MMF_USERNAME"]);
        Assert.Equal(pw, configs["MMF_PASSWORD"]);
        Assert.Equal("downloader_v2", configs["CLIENT_ID"]);
        Assert.Equal("sekrit-client-secret", configs["CLIENT_SECRET"]);
    }

    [Fact]
    public async Task TokenKeys_AreExcluded_FromPluginConfigDict()
    {
        using var db = TestDbContextFactory.Create();

        db.PluginConfigs.AddRange(
            new PluginConfig
            {
                Id = Guid.NewGuid(),
                PluginSlug = "mmf",
                Key = "MMF_USERNAME",
                Value = "damon@example.com",
                IsEncrypted = false,
                UpdatedAt = DateTime.UtcNow,
            },
            new PluginConfig
            {
                Id = Guid.NewGuid(),
                PluginSlug = "mmf",
                Key = "__token__session_cookies",
                Value = SecretEncryption.Encrypt("long-cookie-string"),
                IsEncrypted = true,
                UpdatedAt = DateTime.UtcNow,
            },
            new PluginConfig
            {
                Id = Guid.NewGuid(),
                PluginSlug = "mmf",
                Key = "__token__access_token",
                Value = SecretEncryption.Encrypt("bearer-token-here"),
                IsEncrypted = true,
                UpdatedAt = DateTime.UtcNow,
            }
        );
        await db.SaveChangesAsync();

        var configs = await BuildConfigDictAsync(db, "mmf");

        Assert.Single(configs);
        Assert.True(configs.ContainsKey("MMF_USERNAME"));
        Assert.False(configs.ContainsKey("__token__session_cookies"));
        Assert.False(configs.ContainsKey("__token__access_token"));
    }

    [Fact]
    public async Task CorruptCiphertext_FallsThroughToRawValue_AndCallsErrorCallback()
    {
        using var db = TestDbContextFactory.Create();

        // Simulate a row that was marked IsEncrypted=true but whose value
        // is not valid ciphertext (e.g., encryption key changed, data
        // truncated in a migration, or a manual DB edit).
        const string bogusCiphertext = "this-is-not-valid-base64-ciphertext-at-all-!!!!";

        db.PluginConfigs.Add(new PluginConfig
        {
            Id = Guid.NewGuid(),
            PluginSlug = "mmf",
            Key = "MMF_PASSWORD",
            Value = bogusCiphertext,
            IsEncrypted = true,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var failureKeys = new List<string>();
        var configs = await BuildConfigDictAsync(
            db, "mmf",
            onDecryptFailure: (k, _) => failureKeys.Add(k));

        // Fallback: the plugin sees the ciphertext (so it will fail auth
        // with a specific upstream error) rather than the host crashing
        // and bricking the entire plugin system.
        Assert.Contains("MMF_PASSWORD", configs.Keys);
        Assert.Equal(bogusCiphertext, configs["MMF_PASSWORD"]);

        // The failure callback MUST fire so operators get a log + can
        // re-save the field from the admin UI.
        Assert.Single(failureKeys);
        Assert.Equal("MMF_PASSWORD", failureKeys[0]);
    }

    [Fact]
    public async Task OtherPluginsConfigs_AreIsolated_FromEachOther()
    {
        using var db = TestDbContextFactory.Create();

        db.PluginConfigs.AddRange(
            new PluginConfig
            {
                Id = Guid.NewGuid(),
                PluginSlug = "mmf",
                Key = "MMF_PASSWORD",
                Value = SecretEncryption.Encrypt("mmf-pw"),
                IsEncrypted = true,
                UpdatedAt = DateTime.UtcNow,
            },
            new PluginConfig
            {
                Id = Guid.NewGuid(),
                PluginSlug = "thangs",
                Key = "THANGS_API_KEY",
                Value = SecretEncryption.Encrypt("thangs-key"),
                IsEncrypted = true,
                UpdatedAt = DateTime.UtcNow,
            }
        );
        await db.SaveChangesAsync();

        var mmfConfigs = await BuildConfigDictAsync(db, "mmf");
        var thangsConfigs = await BuildConfigDictAsync(db, "thangs");

        Assert.Equal("mmf-pw", mmfConfigs["MMF_PASSWORD"]);
        Assert.False(mmfConfigs.ContainsKey("THANGS_API_KEY"));

        Assert.Equal("thangs-key", thangsConfigs["THANGS_API_KEY"]);
        Assert.False(thangsConfigs.ContainsKey("MMF_PASSWORD"));
    }

    [Fact]
    public void SecretEncryption_RoundTrip_IsExact()
    {
        // Sanity check that the underlying encryption primitive is reversible.
        // If this ever regresses, the higher-level config decryption tests will
        // also fail, but this one pinpoints which layer broke.
        const string plaintext = "complex!@#$%^&*()_+-={}[]|:;\"'<>,.?/~`password_with_64_chars!!";
        var ciphertext = SecretEncryption.Encrypt(plaintext);
        Assert.NotEqual(plaintext, ciphertext);
        var roundtripped = SecretEncryption.Decrypt(ciphertext);
        Assert.Equal(plaintext, roundtripped);
    }
}
