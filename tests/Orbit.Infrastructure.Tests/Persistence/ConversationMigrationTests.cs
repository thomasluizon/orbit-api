using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class ConversationMigrationTests
{
    [Fact]
    public void Migration_ExpandsSchemaAndDisablesCollection_AndDownDropsDiagnosticData()
    {
        var migration = new PersistConversationTurns();
        migration.UpOperations.Should().HaveCount(6);
        AssertConversationExpansion(migration.UpOperations);
        migration.DownOperations.OfType<DropTableOperation>().Should().ContainSingle().Which.Name.Should().Be("ConversationTurns");
        migration.DownOperations.OfType<DropColumnOperation>().Should().ContainSingle().Which.Name.Should().Be("ConversationHistoryClearedAtUtc");
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        context.Database.HasPendingModelChanges().Should().BeFalse();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var model = context.GetService<IModelRuntimeInitializer>()
            .Initialize(new PreserveProviderPaidCoverage().TargetModel, designTime: true);
        generator.Generate(migration.UpOperations, model).Should().NotBeEmpty();
        generator.Generate(migration.DownOperations, model).Should().NotBeEmpty();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var preceding = initializer.Initialize(new PriceDailyAiBudgetsInUsd().TargetModel, designTime: true);
        var target = initializer.Initialize(migration.TargetModel, designTime: true);
        var differ = context.GetService<IMigrationsModelDiffer>();
        var additions = differ.GetDifferences(preceding.GetRelationalModel(), target.GetRelationalModel());
        additions.Should().HaveCount(6);
        AssertConversationExpansion(additions);
        var remaining = AppStoreMigrationTests.WithoutAppleAdditions(differ.GetDifferences(target.GetRelationalModel(), model.GetRelationalModel()));
        var schemaVersion = remaining.OfType<AddColumnOperation>().Should().ContainSingle(column => column.Table == "PendingAgentOperations").Subject;
        schemaVersion.Table.Should().Be("PendingAgentOperations");
        schemaVersion.Name.Should().Be("ArgumentSchemaVersion");
        schemaVersion.ClrType.Should().Be(typeof(int));
        schemaVersion.IsNullable.Should().BeFalse();
        schemaVersion.DefaultValue.Should().Be(0);
        remaining.Should().HaveCount(11 + RetiredTableCallerTests.AssertCompatibilityForeignKeys(remaining));
        remaining.OfType<AddColumnOperation>().Should().HaveCount(8);
        remaining.OfType<AddColumnOperation>().Should().ContainSingle(column => column.Table == "ConversationTurns"
            && column.Name == "ResponseJson" && column.IsNullable && column.ClrType == typeof(string));
        ExpandHabitNextDueDateMigrationTests.AssertCursorColumn(remaining);
        ReferralRewardMigrationTests.AssertRewardColumns(remaining);
        MakeSubHabitsFreeMigrationTests.AssertFreeFeatureSeed(remaining);
        RestoreProactivePreferenceMigrationTests.AssertRequiredPreference(remaining);
        ExpandHabitScheduleAndStartDateMigrationTests.AssertEvalTraceSeed(remaining);
    }

    internal static void AssertConversationExpansion(IReadOnlyList<MigrationOperation> operations, bool composedResponse = false)
    {
        var table = operations.OfType<CreateTableOperation>().Should().ContainSingle(operation => operation.Name == "ConversationTurns").Subject;
        var columns = new[] { "Id", "UserId", "TurnId", "Role", "CreatedAtUtc", "Model", "Content", "ToolCallsJson" };
        table.Columns.Select(column => column.Name).Should().BeEquivalentTo(composedResponse ? columns.Append("ResponseJson") : columns);
        if (composedResponse)
            table.Columns.Should().ContainSingle(column => column.Name == "ResponseJson" && column.ColumnType == "text" && column.IsNullable);
        table.Columns.Should().ContainSingle(column => column.Name == "Content" && column.ColumnType == "text" && column.IsNullable);
        table.Columns.Should().ContainSingle(column => column.Name == "ToolCallsJson" && column.ColumnType == "text" && !column.IsNullable);
        table.PrimaryKey!.Columns.Should().Equal("Id");
        var foreignKey = table.ForeignKeys.Should().ContainSingle().Subject;
        foreignKey.Columns.Should().Equal("UserId");
        foreignKey.PrincipalTable.Should().Be("Users");
        foreignKey.PrincipalColumns.Should().Equal("Id");
        foreignKey.OnDelete.Should().Be(ReferentialAction.Cascade);
        operations.OfType<AddColumnOperation>().Should().ContainSingle(column => column.Table == "Users"
            && column.Name == "ConversationHistoryClearedAtUtc" && column.IsNullable && column.ClrType == typeof(DateTime));
        var indexes = operations.OfType<CreateIndexOperation>().Where(index => index.Table == "ConversationTurns").ToArray();
        indexes.Should().HaveCount(3);
        indexes.Should().ContainSingle(index => index.Name == "IX_ConversationTurns_CreatedAtUtc"
            && index.Columns.SequenceEqual(new[] { "CreatedAtUtc" }) && !index.IsUnique);
        indexes.Should().ContainSingle(index => index.Name == "IX_ConversationTurns_TurnId_Role"
            && index.Columns.SequenceEqual(new[] { "TurnId", "Role" }) && index.IsUnique);
        indexes.Should().ContainSingle(index => index.Name == "IX_ConversationTurns_UserId_CreatedAtUtc"
            && index.Columns.SequenceEqual(new[] { "UserId", "CreatedAtUtc" }) && !index.IsUnique);
        var seed = operations.OfType<InsertDataOperation>().Should().ContainSingle(operation => operation.Table == "AppFeatureFlags").Subject;
        var row = Enumerable.Range(0, seed.Values.GetLength(0)).Should().ContainSingle(index =>
            Equals(seed.Values[index, Array.IndexOf(seed.Columns, "Key")], "astra_conversation_recording")).Subject;
        seed.Values[row, Array.IndexOf(seed.Columns, "Enabled")].Should().Be(false);
        seed.Values[row, Array.IndexOf(seed.Columns, "PlanRequirement")].Should().BeNull();
    }
}
