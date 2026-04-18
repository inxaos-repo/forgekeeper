using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Forgekeeper.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFileIssues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "file_issues",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    file_path = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    model_id = table.Column<Guid>(type: "uuid", nullable: true),
                    issue_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    first_seen = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_seen = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    dismissed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    dismissed_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    dismissed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_file_issues", x => x.id);
                    table.ForeignKey(
                        name: "fk_file_issues_models_model_id",
                        column: x => x.model_id,
                        principalTable: "models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_file_issues_variants_variant_id",
                        column: x => x.variant_id,
                        principalTable: "variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_file_issues_issue_type",
                table: "file_issues",
                column: "issue_type");

            migrationBuilder.CreateIndex(
                name: "ix_file_issues_file_path_issue_type",
                table: "file_issues",
                columns: new[] { "file_path", "issue_type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "file_issues");
        }
    }
}
