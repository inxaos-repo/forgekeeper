using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Forgekeeper.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceDifferentiation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source_detection",
                table: "models",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_key",
                table: "models",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "canonical_creator_id",
                table: "creators",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_models_source_key",
                table: "models",
                column: "source_key",
                unique: true,
                filter: "source_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_creators_canonical_creator_id",
                table: "creators",
                column: "canonical_creator_id");

            migrationBuilder.AddForeignKey(
                name: "fk_creators_creators_canonical_creator_id",
                table: "creators",
                column: "canonical_creator_id",
                principalTable: "creators",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_creators_creators_canonical_creator_id",
                table: "creators");

            migrationBuilder.DropIndex(
                name: "ux_models_source_key",
                table: "models");

            migrationBuilder.DropIndex(
                name: "ix_creators_canonical_creator_id",
                table: "creators");

            migrationBuilder.DropColumn(
                name: "source_detection",
                table: "models");

            migrationBuilder.DropColumn(
                name: "source_key",
                table: "models");

            migrationBuilder.DropColumn(
                name: "canonical_creator_id",
                table: "creators");
        }
    }
}
