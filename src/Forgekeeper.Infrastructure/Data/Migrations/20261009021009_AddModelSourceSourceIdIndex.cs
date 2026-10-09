using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Forgekeeper.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddModelSourceSourceIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_models_source_source_id",
                table: "models",
                columns: new[] { "source", "source_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_models_source_source_id",
                table: "models");
        }
    }
}
