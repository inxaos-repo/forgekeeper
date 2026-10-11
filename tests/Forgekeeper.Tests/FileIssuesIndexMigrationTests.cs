using Forgekeeper.Infrastructure.Data.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>#56: file_issues FK indexes must be created concurrently, outside a transaction.</summary>
public class FileIssuesIndexMigrationTests
{
    private sealed class Probe : AddFileIssuesFkIndexes
    {
        public List<SqlOperation> UpOps()
        {
            var mb = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
            Up(mb);
            return mb.Operations.Cast<SqlOperation>().ToList();
        }
    }

    [Fact]
    public void Up_CreatesBothIndexesConcurrently_WithoutTransaction()
    {
        var ops = new Probe().UpOps();
        Assert.All(ops, o => Assert.True(o.SuppressTransaction));
        Assert.Contains(ops, o => o.Sql.Contains("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_file_issues_model_id ON file_issues (model_id)"));
        Assert.Contains(ops, o => o.Sql.Contains("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_file_issues_variant_id ON file_issues (variant_id)"));
    }
}
