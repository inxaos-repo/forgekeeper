using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Forgekeeper.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MultiSourceAttributionPhase1to3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "sources",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "website_url",
                table: "sources",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "merged_into_id",
                table: "creators",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "creator_aliases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    creator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alias = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    normalized_alias = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_creator_aliases", x => x.id);
                    table.ForeignKey(
                        name: "fk_creator_aliases_creators_creator_id",
                        column: x => x.creator_id,
                        principalTable: "creators",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "creator_merges",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    survivor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    merged_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    merged_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    merged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    unmerged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_creator_merges", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "creator_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    creator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    platform = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    handle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_creator_profiles", x => x.id);
                    table.ForeignKey(
                        name: "fk_creator_profiles_creators_creator_id",
                        column: x => x.creator_id,
                        principalTable: "creators",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_creator_profiles_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "model_acquisitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    external_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    acquired_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    license = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    tier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    order_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    pledge_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    file_selector = table.Column<string>(type: "jsonb", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_acquisitions", x => x.id);
                    table.ForeignKey(
                        name: "fk_model_acquisitions_models_model_id",
                        column: x => x.model_id,
                        principalTable: "models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_model_acquisitions_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_creators_merged_into_id",
                table: "creators",
                column: "merged_into_id",
                filter: "merged_into_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_creator_aliases_creator_id_normalized_alias",
                table: "creator_aliases",
                columns: new[] { "creator_id", "normalized_alias" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_creator_aliases_normalized_alias",
                table: "creator_aliases",
                column: "normalized_alias");

            migrationBuilder.CreateIndex(
                name: "ix_creator_merges_merged_id",
                table: "creator_merges",
                column: "merged_id");

            migrationBuilder.CreateIndex(
                name: "ix_creator_merges_survivor_id",
                table: "creator_merges",
                column: "survivor_id");

            migrationBuilder.CreateIndex(
                name: "ix_creator_profiles_creator_id",
                table: "creator_profiles",
                column: "creator_id");

            migrationBuilder.CreateIndex(
                name: "ix_creator_profiles_platform_external_id",
                table: "creator_profiles",
                columns: new[] { "platform", "external_id" },
                filter: "external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_creator_profiles_source_id",
                table: "creator_profiles",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "ix_model_acquisitions_model_id",
                table: "model_acquisitions",
                column: "model_id");

            migrationBuilder.CreateIndex(
                name: "ix_model_acquisitions_source_id_kind",
                table: "model_acquisitions",
                columns: new[] { "source_id", "kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "creator_aliases");

            migrationBuilder.DropTable(
                name: "creator_merges");

            migrationBuilder.DropTable(
                name: "creator_profiles");

            migrationBuilder.DropTable(
                name: "model_acquisitions");

            migrationBuilder.DropIndex(
                name: "ix_creators_merged_into_id",
                table: "creators");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "website_url",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "merged_into_id",
                table: "creators");
        }
    }
}
