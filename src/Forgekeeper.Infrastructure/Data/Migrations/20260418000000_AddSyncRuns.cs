using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Forgekeeper.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sync_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plugin_slug = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    total_models = table.Column<int>(type: "integer", nullable: false),
                    scraped_models = table.Column<int>(type: "integer", nullable: false),
                    failed_models = table.Column<int>(type: "integer", nullable: false),
                    skipped_models = table.Column<int>(type: "integer", nullable: false),
                    files_downloaded = table.Column<int>(type: "integer", nullable: false),
                    bytes_downloaded = table.Column<long>(type: "bigint", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    duration_seconds = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_runs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sync_runs_plugin_slug",
                table: "sync_runs",
                column: "plugin_slug");

            migrationBuilder.CreateIndex(
                name: "ix_sync_runs_started_at",
                table: "sync_runs",
                column: "started_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "sync_runs");
        }
    }
}
