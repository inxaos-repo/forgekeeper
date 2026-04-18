using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Forgekeeper.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScalabilityIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Partial index on variant.file_hash for deduplication queries.
            // Without this, GET /api/v1/models/duplicates performs a full table scan on variants.
            // At 500K variants this degraded from <100ms to multiple seconds.
            migrationBuilder.CreateIndex(
                name: "ix_variants_file_hash",
                table: "variants",
                column: "file_hash",
                filter: "file_hash IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_variants_file_hash",
                table: "variants");
        }
    }
}
