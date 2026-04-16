using System.Text.Json;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Forgekeeper.Tests;

/// <summary>
/// Factory for creating InMemory DbContext instances for testing.
/// Handles the JSONB type incompatibility by providing value converters.
/// </summary>
public static class TestDbContextFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static ForgeDbContext Create(string? dbName = null)
    {
        dbName ??= $"TestDb_{Guid.NewGuid()}";

        var options = new DbContextOptionsBuilder<ForgeDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        var db = new TestForgeDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}

/// <summary>
/// Test-specific DbContext that overrides model configuration to work with InMemory provider.
/// Adds JSON serialization converters for complex types that Npgsql handles natively via JSONB.
/// </summary>
internal class TestForgeDbContext : ForgeDbContext
{
    public TestForgeDbContext(DbContextOptions<ForgeDbContext> options) : base(options) { }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Override JSONB columns with JSON string converters for InMemory provider
        modelBuilder.Entity<Model3D>(entity =>
        {
            entity.Property(e => e.PrintHistory)
                .HasColumnType(null!) // Clear the jsonb type
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, JsonOpts),
                    v => v == null ? null : JsonSerializer.Deserialize<List<PrintHistoryEntry>>(v, JsonOpts));

            entity.Property(e => e.Components)
                .HasColumnType(null!)
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, JsonOpts),
                    v => v == null ? null : JsonSerializer.Deserialize<List<ComponentInfo>>(v, JsonOpts));

            entity.Property(e => e.PrintSettings)
                .HasColumnType(null!)
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, JsonOpts),
                    v => v == null ? null : JsonSerializer.Deserialize<PrintSettingsInfo>(v, JsonOpts));

            entity.Property(e => e.Extra)
                .HasColumnType(null!);

            entity.Property(e => e.PreviewImages)
                .HasColumnType(null!)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, JsonOpts),
                    v => JsonSerializer.Deserialize<List<string>>(v, JsonOpts) ?? new List<string>());
        });

        modelBuilder.Entity<Variant>(entity =>
        {
            entity.Property(e => e.PhysicalProperties)
                .HasColumnType(null!)
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, JsonOpts),
                    v => v == null ? null : JsonSerializer.Deserialize<PhysicalProperties>(v, JsonOpts));
        });
    }
}
