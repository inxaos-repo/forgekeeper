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
    public DbSet<ModelRelation> ModelRelations => Set<ModelRelation>();
    public DbSet<PluginConfig> PluginConfigs => Set<PluginConfig>();
    public DbSet<SavedTemplate> SavedTemplates => Set<SavedTemplate>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();
    public DbSet<FileIssue> FileIssues => Set<FileIssue>();

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
            entity.Property(e => e.AcquisitionOrderId).HasMaxLength(200);
            entity.Property(e => e.PrintStatus).HasMaxLength(100);

            // Enum stored as string
            entity.Property(e => e.AcquisitionMethod)
                .HasConversion<string>()
                .HasMaxLength(50);

            // JSONB columns
            entity.Property(e => e.PrintHistory).HasColumnType("jsonb");
            entity.Property(e => e.Components).HasColumnType("jsonb");
            entity.Property(e => e.PrintSettings).HasColumnType("jsonb");

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

            // Indexes
            entity.HasIndex(e => e.BasePath).IsUnique();
            entity.HasIndex(e => e.CreatorId);
            entity.HasIndex(e => e.Source);
            entity.HasIndex(e => e.LicenseType);
            entity.HasIndex(e => e.CollectionName);
            entity.HasIndex(e => e.AcquisitionMethod);

            // Partial indexes for nullable filter columns
            entity.HasIndex(e => e.Category)
                .HasFilter("category IS NOT NULL");
            entity.HasIndex(e => e.GameSystem)
                .HasFilter("game_system IS NOT NULL");

            // pg_trgm GIN index on name for fuzzy search
            entity.HasIndex(e => e.Name)
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");

            // GIN index on extra JSONB for metadata queries
            entity.HasIndex(e => e.Extra)
                .HasMethod("gin")
                .HasOperators("jsonb_path_ops");
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
            entity.Property(e => e.FileHash).HasMaxLength(200);

            // JSONB column for physical properties
            entity.Property(e => e.PhysicalProperties).HasColumnType("jsonb");

            entity.HasOne(e => e.Model)
                .WithMany(m => m.Variants)
                .HasForeignKey(e => e.ModelId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.ModelId);
            entity.HasIndex(e => new { e.ModelId, e.FilePath }).IsUnique();

            // Partial index for deduplication queries on FileHash — avoids full table scan on duplicates check
            entity.HasIndex(e => e.FileHash).HasFilter("file_hash IS NOT NULL");
        });

        // Tag
        modelBuilder.Entity<Tag>(entity =>
        {
            entity.ToTable("tags");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Source).HasMaxLength(50);
            entity.HasIndex(e => e.Name).IsUnique();
        });

        // ModelRelation (WP18: self-referencing many-to-many)
        modelBuilder.Entity<ModelRelation>(entity =>
        {
            entity.ToTable("model_relations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.RelationType).HasMaxLength(50).IsRequired();

            entity.HasOne(e => e.Model)
                .WithMany(m => m.RelationsFrom)
                .HasForeignKey(e => e.ModelId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.RelatedModel)
                .WithMany(m => m.RelationsTo)
                .HasForeignKey(e => e.RelatedModelId)
                .OnDelete(DeleteBehavior.Cascade);

            // Prevent duplicate relations
            entity.HasIndex(e => new { e.ModelId, e.RelatedModelId, e.RelationType }).IsUnique();
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

        // PluginConfig
        modelBuilder.Entity<PluginConfig>(entity =>
        {
            entity.ToTable("plugin_configs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.PluginSlug).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Key).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Value).HasColumnType("text").IsRequired();
            entity.HasIndex(e => new { e.PluginSlug, e.Key }).IsUnique();
        });

        // Saved Templates (for filename parsing and directory reorganization)
        modelBuilder.Entity<SavedTemplate>(entity =>
        {
            entity.ToTable("saved_templates");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Template).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Type).HasMaxLength(50).HasDefaultValue("parse");
            entity.Property(e => e.CreatorName).HasMaxLength(200);
            entity.Property(e => e.SourceSlug).HasMaxLength(100);
            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.Type);
        });

        // SyncRun
        modelBuilder.Entity<SyncRun>(entity =>
        {
            entity.ToTable("sync_runs");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PluginSlug);
            entity.HasIndex(e => e.StartedAt);
        });

        // FileIssue
        modelBuilder.Entity<FileIssue>(entity =>
        {
            entity.ToTable("file_issues");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.FilePath).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.IssueType).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ErrorMessage).HasColumnType("text");
            entity.Property(e => e.DismissedBy).HasMaxLength(200);

            entity.HasOne(e => e.Variant)
                .WithMany()
                .HasForeignKey(e => e.VariantId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Model)
                .WithMany()
                .HasForeignKey(e => e.ModelId)
                .OnDelete(DeleteBehavior.SetNull);

            // Unique constraint for upsert: one record per (FilePath, IssueType)
            entity.HasIndex(e => new { e.FilePath, e.IssueType }).IsUnique();
            entity.HasIndex(e => e.IssueType);
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
