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
    public void Migration_PreservesExistingFlagAndCreatesTheConversationIndexConcurrently()
    {
        var migration = new HardenAstraTurnReads();
        migration.UpOperations.OfType<AddColumnOperation>().Should().ContainSingle().Which.Name.Should().Be("ModelContextJson");
        var sql = migration.UpOperations.OfType<SqlOperation>().ToArray();
        sql.Should().ContainSingle(operation => operation.Sql.Contains("ON CONFLICT (\"Key\") DO NOTHING", StringComparison.Ordinal));
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
