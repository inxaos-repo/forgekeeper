using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Forgekeeper.Infrastructure.Data.Migrations;

public partial class AddPluginConfigs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Only create if not exists (for fresh DBs the Initial migration now includes it,
        // but existing DBs need this migration)
        migrationBuilder.Sql(@"
            CREATE TABLE IF NOT EXISTS plugin_configs (
                id uuid NOT NULL DEFAULT gen_random_uuid(),
                plugin_slug character varying(100) NOT NULL,
                key character varying(200) NOT NULL,
                value text NOT NULL,
                is_encrypted boolean NOT NULL DEFAULT false,
                updated_at timestamp with time zone NOT NULL,
                CONSTRAINT pk_plugin_configs PRIMARY KEY (id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_plugin_configs_plugin_slug_key 
                ON plugin_configs (plugin_slug, key);
        ");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "plugin_configs");
    }
}
