using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class ChatTurnMigrationTests
{
    [Fact]
    public void Migration_OnlyAddsTurnLedgerAndUniqueIdentity_AndMatchesModelTransition()
    {
        var migration = new PersistChatTurnIdempotency();
        AssertExpansion(migration.UpOperations);
        migration.DownOperations.Should().ContainSingle().Which.Should().BeOfType<DropTableOperation>()
            .Which.Name.Should().Be("ChatTurns");
        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var initializer = db.GetService<IModelRuntimeInitializer>();
        var previous = initializer.Initialize(new DropUserHandleColumn().TargetModel, designTime: true);
        var target = initializer.Initialize(migration.TargetModel, designTime: true);
        var differ = db.GetService<IMigrationsModelDiffer>();
        AssertExpansion(differ.GetDifferences(previous.GetRelationalModel(), target.GetRelationalModel()));
        db.Database.HasPendingModelChanges().Should().BeFalse();
        db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, target).Should().NotBeEmpty();
    }

    internal static void AssertExpansion(IReadOnlyList<MigrationOperation> operations)
    {
        operations.Should().HaveCount(2);
        var table = operations.OfType<CreateTableOperation>().Should().ContainSingle().Subject;
        table.Name.Should().Be("ChatTurns");
        table.Columns.Select(column => column.Name).Should().BeEquivalentTo(
            "Id", "UserId", "KeyHash", "Fingerprint", "ExpiresAtUtc", "TerminalEvent");
        table.Columns.Should().ContainSingle(column => column.Name == "TerminalEvent" && column.IsNullable && column.ColumnType == "text");
        table.ForeignKeys.Should().ContainSingle().Which.OnDelete.Should().Be(ReferentialAction.Cascade);
        var index = operations.OfType<CreateIndexOperation>().Should().ContainSingle().Subject;
        index.Table.Should().Be("ChatTurns");
        index.IsUnique.Should().BeTrue();
        index.Columns.Should().Equal("UserId", "KeyHash");
    }
}
