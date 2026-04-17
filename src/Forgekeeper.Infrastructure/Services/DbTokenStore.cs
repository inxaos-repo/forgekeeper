using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.PluginSdk;
using Microsoft.EntityFrameworkCore;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// ITokenStore implementation that persists tokens as encrypted PluginConfig entries.
/// </summary>
public class DbTokenStore : ITokenStore
{
    private readonly IDbContextFactory<ForgeDbContext> _dbFactory;
    private readonly string _pluginSlug;

    public DbTokenStore(IDbContextFactory<ForgeDbContext> dbFactory, string pluginSlug)
    {
        _dbFactory = dbFactory;
        _pluginSlug = pluginSlug;
    }

    public async Task<string?> GetTokenAsync(string key, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var tokenKey = $"__token__{key}";
        var entry = await db.PluginConfigs
            .FirstOrDefaultAsync(c => c.PluginSlug == _pluginSlug && c.Key == tokenKey, ct);
        if (entry?.Value == null) return null;
        try
        {
            return SecretEncryption.Decrypt(entry.Value);
        }
        catch
        {
            // Fallback: return raw value if not encrypted (migration period)
            return entry.Value;
        }
    }

    public async Task SaveTokenAsync(string key, string value, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var tokenKey = $"__token__{key}";
        var entry = await db.PluginConfigs
            .FirstOrDefaultAsync(c => c.PluginSlug == _pluginSlug && c.Key == tokenKey, ct);

        if (entry is null)
        {
            entry = new PluginConfig
            {
                Id = Guid.NewGuid(),
                PluginSlug = _pluginSlug,
                Key = tokenKey,
                Value = SecretEncryption.Encrypt(value),
                IsEncrypted = true,
                UpdatedAt = DateTime.UtcNow,
            };
            db.PluginConfigs.Add(entry);
        }
        else
        {
            entry.Value = SecretEncryption.Encrypt(value);
            entry.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteTokenAsync(string key, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var tokenKey = $"__token__{key}";
        var entry = await db.PluginConfigs
            .FirstOrDefaultAsync(c => c.PluginSlug == _pluginSlug && c.Key == tokenKey, ct);

        if (entry is not null)
        {
            db.PluginConfigs.Remove(entry);
            await db.SaveChangesAsync(ct);
        }
    }
}
