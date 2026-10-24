using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class TransactionCommitMigrationTests
{
    [Fact]
    public void Migration_AddsOnlyVerificationReceiptsAndMatchesSnapshot()
    {
        var migration = new AddTransactionCommitVerification();
        migration.UpOperations.Should().HaveCount(2);
        var table = migration.UpOperations.OfType<CreateTableOperation>().Should().ContainSingle().Subject;
        table.Name.Should().Be("TransactionCommits");
        table.Columns.Select(column => column.Name).Should().BeEquivalentTo("Id", "CreatedAtUtc");
        table.Columns.Should().OnlyContain(column => !column.IsNullable);
        table.ForeignKeys.Should().BeEmpty();
        var index = migration.UpOperations.OfType<CreateIndexOperation>().Should().ContainSingle().Subject;
        index.Table.Should().Be("TransactionCommits");
        index.Columns.Should().Equal("CreatedAtUtc");
        migration.DownOperations.Should().ContainSingle().Which.Should().BeOfType<DropTableOperation>()
            .Which.Name.Should().Be("TransactionCommits");

        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var initializer = db.GetService<IModelRuntimeInitializer>();
        var previous = initializer.Initialize(new PersistChatTurnIdempotency().TargetModel, designTime: true);
        var target = initializer.Initialize(migration.TargetModel, designTime: true);
        var current = db.GetService<IDesignTimeModel>().Model;
        var snapshot = initializer.Initialize(new OrbitDbContextModelSnapshot().Model, designTime: true);
        var differ = db.GetService<IMigrationsModelDiffer>();
        differ.GetDifferences(previous.GetRelationalModel(), target.GetRelationalModel())
            .Should().BeEquivalentTo(migration.UpOperations, options => options.PreferringRuntimeMemberTypes());
        differ.GetDifferences(current.GetRelationalModel(), snapshot.GetRelationalModel()).Should().BeEmpty();
        db.Database.HasPendingModelChanges().Should().BeFalse();
    }
}
