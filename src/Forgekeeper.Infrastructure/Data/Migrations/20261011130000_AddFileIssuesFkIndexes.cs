using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Forgekeeper.Infrastructure.Data.Migrations
{
    /// <summary>
    /// #56: file_issues.model_id / variant_id are ON DELETE SET NULL foreign keys, but the
    /// indexes that the model snapshot already declares (ix_file_issues_model_id,
    /// ix_file_issues_variant_id) were never created by AddFileIssues. Every model/variant
    /// delete therefore seq-scanned file_issues (~204k rows).
    ///
    /// Built CONCURRENTLY (no write lock on file_issues while the scanner is running),
    /// which can't run inside a transaction, hence suppressTransaction. IF NOT EXISTS keeps
    /// it idempotent; a CONCURRENTLY build that was interrupted leaves an INVALID index, so
    /// any invalid leftover is dropped first.
    /// </summary>
    public partial class AddFileIssuesFkIndexes : Migration
    {
        private static string DropIfInvalid(string name) => $"""
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
                           WHERE c.relname = '{name}' AND NOT i.indisvalid) THEN
                    EXECUTE 'DROP INDEX {name}';
                END IF;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (name, column) in new[] { ("ix_file_issues_model_id", "model_id"), ("ix_file_issues_variant_id", "variant_id") })
            {
                migrationBuilder.Sql(DropIfInvalid(name), suppressTransaction: true);
                migrationBuilder.Sql(
                    $"CREATE INDEX CONCURRENTLY IF NOT EXISTS {name} ON file_issues ({column});",
                    suppressTransaction: true);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_file_issues_variant_id;", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_file_issues_model_id;", suppressTransaction: true);
        }
    }
}
