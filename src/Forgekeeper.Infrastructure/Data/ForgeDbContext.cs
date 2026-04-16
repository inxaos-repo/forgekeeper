using Forgekeeper.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Forgekeeper.Infrastructure.Data;

public class ForgeDbContext : DbContext
{
    public ForgeDbContext(DbContextOptions<ForgeDbContext> options) : base(options) { }

    public DbSet<Creator> Creators => Set<Creator>();
    public DbSet<Model3D> Models => Set<Model3D>();
    public DbSet<Variant> Variants => Set<Variant>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<ImportQueueItem> ImportQueue => Set<ImportQueueItem>();
    public DbSet<ScanState> ScanStates => Set<ScanState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Enable pg_trgm extension
        modelBuilder.HasPostgresExtension("pg_trgm");

        // Creator
        modelBuilder.Entity<Creator>(entity =>
        {
            entity.ToTable("creators");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Name).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Source).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.SourceUrl).HasMaxLength(1000);
            entity.Property(e => e.ExternalId).HasMaxLength(200);
            entity.Property(e => e.AvatarUrl).HasMaxLength(1000);

            entity.HasIndex(e => new { e.Name, e.Source }).IsUnique();
            entity.HasIndex(e => e.Name)
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");
        });

        // Model3D
        modelBuilder.Entity<Model3D>(entity =>
        {
            entity.ToTable("models");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Name).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.SourceId).HasMaxLength(200);
            entity.Property(e => e.Source).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.SourceUrl).HasMaxLength(1000);
            entity.Property(e => e.Description).HasColumnType("text");
            entity.Property(e => e.Category).HasMaxLength(200);
            entity.Property(e => e.Scale).HasMaxLength(50);
            entity.Property(e => e.GameSystem).HasMaxLength(200);
            entity.Property(e => e.ThumbnailPath).HasMaxLength(1000);
            entity.Property(e => e.PreviewImages).HasColumnType("text[]");
            entity.Property(e => e.BasePath).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.Notes).HasColumnType("text");
            entity.Property(e => e.Extra).HasColumnType("jsonb");
            entity.Property(e => e.LicenseType).HasMaxLength(100);
            entity.Property(e => e.CollectionName).HasMaxLength(500);

            // JSONB columns
            entity.Property(e => e.PrintHistory).HasColumnType("jsonb");
            entity.Property(e => e.Components).HasColumnType("jsonb");

            // Printed is a computed property, ignore it for EF
            entity.Ignore(e => e.Printed);

            entity.Property(e => e.SourceEntityId).HasColumnName("source_entity_id");

            entity.HasOne(e => e.Creator)
                .WithMany(c => c.Models)
                .HasForeignKey(e => e.CreatorId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.SourceEntity)
                .WithMany(s => s.Models)
                .HasForeignKey(e => e.SourceEntityId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(e => e.Tags)
                .WithMany(t => t.Models)
                .UsingEntity("model_tags",
                    l => l.HasOne(typeof(Tag)).WithMany().HasForeignKey("TagId"),
                    r => r.HasOne(typeof(Model3D)).WithMany().HasForeignKey("ModelId"));

            entity.HasIndex(e => e.BasePath).IsUnique();
            entity.HasIndex(e => e.CreatorId);
            entity.HasIndex(e => e.Source);
            entity.HasIndex(e => e.Category);
            entity.HasIndex(e => e.GameSystem);
            entity.HasIndex(e => e.LicenseType);
            entity.HasIndex(e => e.CollectionName);
            entity.HasIndex(e => e.Name)
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");
        });

        // Variant
        modelBuilder.Entity<Variant>(entity =>
        {
            entity.ToTable("variants");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.VariantType).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.FilePath).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.FileName).HasMaxLength(500).IsRequired();
            entity.Property(e => e.FileType).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.ThumbnailPath).HasMaxLength(1000);

            // JSONB column for physical properties
            entity.Property(e => e.PhysicalProperties).HasColumnType("jsonb");

            entity.HasOne(e => e.Model)
                .WithMany(m => m.Variants)
                .HasForeignKey(e => e.ModelId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.ModelId);
            entity.HasIndex(e => e.FilePath).IsUnique();
        });

        // Tag
        modelBuilder.Entity<Tag>(entity =>
        {
            entity.ToTable("tags");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.HasIndex(e => e.Name).IsUnique();
        });

        // ImportQueueItem
        modelBuilder.Entity<ImportQueueItem>(entity =>
        {
            entity.ToTable("import_queue");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.OriginalPath).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.DetectedCreator).HasMaxLength(500);
            entity.Property(e => e.DetectedModelName).HasMaxLength(1000);
            entity.Property(e => e.DetectedSource).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.DetectedVariantType).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.ConfirmedCreator).HasMaxLength(500);
            entity.Property(e => e.ConfirmedModelName).HasMaxLength(1000);
            entity.Property(e => e.ConfirmedSource).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.ErrorMessage).HasColumnType("text");

            entity.HasIndex(e => e.Status);
        });

        // Source
        modelBuilder.Entity<Source>(entity =>
        {
            entity.ToTable("sources");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Slug).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(500).IsRequired();
            entity.Property(e => e.BasePath).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.AdapterType).HasMaxLength(200).IsRequired();
            entity.HasIndex(e => e.Slug).IsUnique();
        });

        // ScanState
        modelBuilder.Entity<ScanState>(entity =>
        {
            entity.ToTable("scan_states");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.DirectoryPath).HasMaxLength(2000).IsRequired();
            entity.HasIndex(e => e.DirectoryPath).IsUnique();
        });
    }
}
