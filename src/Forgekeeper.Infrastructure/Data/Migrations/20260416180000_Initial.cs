using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable enable

namespace Forgekeeper.Infrastructure.Data.Migrations;

/// <inheritdoc />
public partial class Initial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Enable pg_trgm extension
        migrationBuilder.AlterDatabase()
            .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

        // Creators table
        migrationBuilder.CreateTable(
            name: "creators",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                source_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                avatar_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                model_count = table.Column<int>(type: "integer", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_creators", x => x.id);
            });

        // Sources table
        migrationBuilder.CreateTable(
            name: "sources",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                base_path = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                adapter_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                auto_scan = table.Column<bool>(type: "boolean", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_sources", x => x.id);
            });

        // Tags table
        migrationBuilder.CreateTable(
            name: "tags",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_tags", x => x.id);
            });

        // Scan states table
        migrationBuilder.CreateTable(
            name: "scan_states",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                directory_path = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                last_scanned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                last_modified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                file_count = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_scan_states", x => x.id);
            });

        // Import queue table
        migrationBuilder.CreateTable(
            name: "import_queue",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                original_path = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                detected_creator = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                detected_model_name = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                detected_source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                detected_variant_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                confidence_score = table.Column<double>(type: "double precision", nullable: false),
                status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                confirmed_creator = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                confirmed_model_name = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                confirmed_source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                error_message = table.Column<string>(type: "text", nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_import_queue", x => x.id);
            });

        // Models table
        migrationBuilder.CreateTable(
            name: "models",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                creator_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                source_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                source_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                source_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                description = table.Column<string>(type: "text", nullable: true),
                category = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                scale = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                game_system = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                file_count = table.Column<int>(type: "integer", nullable: false),
                total_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                thumbnail_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                preview_images = table.Column<string[]>(type: "text[]", nullable: false),
                base_path = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                rating = table.Column<int>(type: "integer", nullable: true),
                notes = table.Column<string>(type: "text", nullable: true),
                extra = table.Column<string>(type: "jsonb", nullable: true),
                print_history = table.Column<string>(type: "jsonb", nullable: true),
                components = table.Column<string>(type: "jsonb", nullable: true),
                print_settings = table.Column<string>(type: "jsonb", nullable: true),
                license_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                collection_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                acquisition_method = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                acquisition_order_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                external_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                external_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                downloaded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                last_scanned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_models", x => x.id);
                table.ForeignKey(
                    name: "fk_models_creators_creator_id",
                    column: x => x.creator_id,
                    principalTable: "creators",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_models_sources_source_entity_id",
                    column: x => x.source_entity_id,
                    principalTable: "sources",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
            });

        // Variants table
        migrationBuilder.CreateTable(
            name: "variants",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                model_id = table.Column<Guid>(type: "uuid", nullable: false),
                variant_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                file_path = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                file_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                file_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                file_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                thumbnail_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                file_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                physical_properties = table.Column<string>(type: "jsonb", nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_variants", x => x.id);
                table.ForeignKey(
                    name: "fk_variants_models_model_id",
                    column: x => x.model_id,
                    principalTable: "models",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        // Model tags junction table
        migrationBuilder.CreateTable(
            name: "model_tags",
            columns: table => new
            {
                ModelId = table.Column<Guid>(type: "uuid", nullable: false),
                TagId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_model_tags", x => new { x.ModelId, x.TagId });
                table.ForeignKey(
                    name: "fk_model_tags_models_model_id",
                    column: x => x.ModelId,
                    principalTable: "models",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_model_tags_tags_tag_id",
                    column: x => x.TagId,
                    principalTable: "tags",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        // Model relations table (WP18: self-referencing many-to-many)
        migrationBuilder.CreateTable(
            name: "model_relations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                model_id = table.Column<Guid>(type: "uuid", nullable: false),
                related_model_id = table.Column<Guid>(type: "uuid", nullable: false),
                relation_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_model_relations", x => x.id);
                table.ForeignKey(
                    name: "fk_model_relations_models_model_id",
                    column: x => x.model_id,
                    principalTable: "models",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_model_relations_models_related_model_id",
                    column: x => x.related_model_id,
                    principalTable: "models",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        // === INDEXES ===

        // Creators
        migrationBuilder.CreateIndex(
            name: "ix_creators_name_source",
            table: "creators",
            columns: new[] { "name", "source" },
            unique: true);

        migrationBuilder.Sql(
            "CREATE INDEX idx_creator_name_trgm ON creators USING gin (name gin_trgm_ops);");

        // Models
        migrationBuilder.CreateIndex(
            name: "ix_models_base_path",
            table: "models",
            column: "base_path",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_models_creator_id",
            table: "models",
            column: "creator_id");

        migrationBuilder.CreateIndex(
            name: "ix_models_source",
            table: "models",
            column: "source");

        migrationBuilder.CreateIndex(
            name: "ix_models_license_type",
            table: "models",
            column: "license_type");

        migrationBuilder.CreateIndex(
            name: "ix_models_collection_name",
            table: "models",
            column: "collection_name");

        migrationBuilder.CreateIndex(
            name: "ix_models_acquisition_method",
            table: "models",
            column: "acquisition_method");

        migrationBuilder.CreateIndex(
            name: "ix_models_source_entity_id",
            table: "models",
            column: "source_entity_id");

        // Partial indexes for nullable filter columns
        migrationBuilder.Sql(
            "CREATE INDEX ix_models_category ON models (category) WHERE category IS NOT NULL;");

        migrationBuilder.Sql(
            "CREATE INDEX ix_models_game_system ON models (game_system) WHERE game_system IS NOT NULL;");

        // pg_trgm and JSONB indexes
        migrationBuilder.Sql(
            "CREATE INDEX idx_model_name_trgm ON models USING gin (name gin_trgm_ops);");

        migrationBuilder.Sql(
            "CREATE INDEX idx_model_extra ON models USING gin (extra jsonb_path_ops);");

        // Variants
        migrationBuilder.CreateIndex(
            name: "ix_variants_model_id",
            table: "variants",
            column: "model_id");

        migrationBuilder.CreateIndex(
            name: "ix_variants_file_path",
            table: "variants",
            column: "file_path",
            unique: true);

        // Tags
        migrationBuilder.CreateIndex(
            name: "ix_tags_name",
            table: "tags",
            column: "name",
            unique: true);

        // Model tags
        migrationBuilder.CreateIndex(
            name: "ix_model_tags_tag_id",
            table: "model_tags",
            column: "TagId");

        // Model relations
        migrationBuilder.CreateIndex(
            name: "ix_model_relations_model_id_related_model_id_relation_type",
            table: "model_relations",
            columns: new[] { "model_id", "related_model_id", "relation_type" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_model_relations_related_model_id",
            table: "model_relations",
            column: "related_model_id");

        // Sources
        migrationBuilder.CreateIndex(
            name: "ix_sources_slug",
            table: "sources",
            column: "slug",
            unique: true);

        // Scan states
        migrationBuilder.CreateIndex(
            name: "ix_scan_states_directory_path",
            table: "scan_states",
            column: "directory_path",
            unique: true);

        // Import queue
        migrationBuilder.CreateIndex(
            name: "ix_import_queue_status",
            table: "import_queue",
            column: "status");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "model_relations");
        migrationBuilder.DropTable(name: "model_tags");
        migrationBuilder.DropTable(name: "variants");
        migrationBuilder.DropTable(name: "models");
        migrationBuilder.DropTable(name: "tags");
        migrationBuilder.DropTable(name: "creators");
        migrationBuilder.DropTable(name: "sources");
        migrationBuilder.DropTable(name: "scan_states");
        migrationBuilder.DropTable(name: "import_queue");
    }
}
