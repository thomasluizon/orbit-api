using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class HardenAstraTurnReadsMigrationTests
{
    [Fact]
    public void Migration_RetryAfterIndexFailure_UsesIdempotentColumnSql()
    {
        var migration = new HardenAstraTurnReads();
        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>().UseNpgsql("Host=unused;Database=unused").Options);
        var model = db.GetService<IModelRuntimeInitializer>().Initialize(migration.TargetModel, designTime: true);
        var commands = db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, model);

        commands.Should().HaveCount(2);
        commands[0].CommandText.Should().Contain("ADD COLUMN IF NOT EXISTS \"ModelContextJson\" text",
            "the column is committed before the concurrent index and must survive retrying an unrecorded migration");
        commands[1].TransactionSuppressed.Should().BeTrue();
        commands[1].CommandText.Should().Contain("CREATE INDEX CONCURRENTLY IF NOT EXISTS");
        RollingMigrationGate.Check("20261102074154_HardenAstraTurnReads", migration.UpOperations).Should().BeEmpty();
    }

    [Fact]
    public void Migration_PreservesExistingFlagAndCreatesTheConversationIndexConcurrently()
    {
        var migration = new HardenAstraTurnReads();
        migration.UpOperations.OfType<AddColumnOperation>().Should().BeEmpty();
        var sql = migration.UpOperations.OfType<SqlOperation>().ToArray();
        sql.Should().ContainSingle(operation => operation.Sql.Contains("ADD COLUMN IF NOT EXISTS \"ModelContextJson\" text", StringComparison.Ordinal));
        sql.Should().NotContain(operation => operation.Sql.Contains("AppFeatureFlags", StringComparison.Ordinal),
            "the existing seed and enable migrations own the flag and its current value");
        sql.Should().ContainSingle(operation => operation.SuppressTransaction
            && operation.Sql.Contains("CREATE INDEX CONCURRENTLY IF NOT EXISTS", StringComparison.Ordinal)
            && operation.Sql.Contains("(\"ConversationId\", \"UserId\")", StringComparison.Ordinal));
        migration.DownOperations.OfType<DeleteDataOperation>().Should().BeEmpty("the flag predates this migration");
        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>().UseNpgsql("Host=unused;Database=unused").Options);
        db.Database.HasPendingModelChanges().Should().BeFalse();
        var model = db.GetService<IModelRuntimeInitializer>().Initialize(migration.TargetModel, designTime: true);
        db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, model).Should().NotBeEmpty();
    }
}
