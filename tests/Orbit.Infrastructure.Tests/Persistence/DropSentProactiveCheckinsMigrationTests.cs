using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class DropSentProactiveCheckinsMigrationTests
{
    [Fact]
    public void Migration_FollowsRetiredFeatureContraction()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var migrations = context.GetService<IMigrationsAssembly>().Migrations;
        var retiredFeatures = migrations.Single(pair => pair.Value.AsType() == typeof(DropRetiredFeatureTables)).Key;
        var retiredLedger = migrations.Single(pair => pair.Value.AsType() == typeof(DropSentProactiveCheckins)).Key;

        migrations.Keys.Should().ContainInOrder(retiredFeatures, retiredLedger);
    }

    [Fact]
    public void CurrentModelAndSnapshot_ExcludeRetiredLedgerAndPreserveProactiveTables()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        foreach (var model in new[] { factory.Context.Model, new DropSentProactiveCheckins().TargetModel, new OrbitDbContextModelSnapshot().Model })
        {
            var tables = model.GetEntityTypes().Select(entity => entity.GetTableName()).ToArray();
            tables.Should().NotContain("SentProactiveCheckins");
            tables.Should().NotIntersectWith(new DropRetiredFeatureTables().UpOperations
                .OfType<DropTableOperation>().Select(operation => operation.Name));
            tables.Should().Contain("ProactiveItems").And.Contain("ProactiveCalendarDays");
        }
    }

    [Fact]
    public void Up_DropsOnlyRetiredLedger_AndModelsMatchThatOperation()
    {
        var migration = new DropSentProactiveCheckins();
        migration.UpOperations.Should().ContainSingle();
        AssertRetiredLedgerDrop(migration.UpOperations);

        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var previous = initializer.Initialize(new DropRetiredFeatureTables().TargetModel, designTime: true);
        var target = initializer.Initialize(migration.TargetModel, designTime: true);
        var current = context.GetService<IDesignTimeModel>().Model;
        var differ = context.GetService<IMigrationsModelDiffer>();
        var changes = differ.GetDifferences(previous.GetRelationalModel(), target.GetRelationalModel());
        changes.Should().ContainSingle();
        AssertRetiredLedgerDrop(changes);
        context.Database.HasPendingModelChanges().Should().BeFalse();

        var sql = context.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, current);
        sql.Should().ContainSingle().Which.CommandText.Trim().Should().Be("DROP TABLE \"SentProactiveCheckins\";");
    }

    [Fact]
    public void Down_RestoresOnlyRetiredLedgerSchemaWithoutChangingProactiveTables()
    {
        var operations = new DropSentProactiveCheckins().DownOperations;
        operations.Should().HaveCount(2);
        var table = operations.OfType<CreateTableOperation>().Should().ContainSingle().Subject;
        table.Name.Should().Be("SentProactiveCheckins");
        table.Columns.Select(column => column.Name).Should().BeEquivalentTo("Id", "UserId", "Date", "SentAtUtc");
        table.PrimaryKey!.Columns.Should().Equal("Id");
        var foreignKey = table.ForeignKeys.Should().ContainSingle().Subject;
        foreignKey.PrincipalTable.Should().Be("Users");
        foreignKey.Columns.Should().Equal("UserId");
        foreignKey.OnDelete.Should().Be(ReferentialAction.Cascade);
        var index = operations.OfType<CreateIndexOperation>().Should().ContainSingle().Subject;
        index.Table.Should().Be("SentProactiveCheckins");
        index.Columns.Should().Equal("UserId", "Date");
        index.IsUnique.Should().BeTrue();
    }

    internal static void AssertRetiredLedgerDrop(IReadOnlyList<MigrationOperation> operations)
    {
        operations.OfType<DropTableOperation>().Should().ContainSingle().Which.Name.Should().Be("SentProactiveCheckins");
    }
}
