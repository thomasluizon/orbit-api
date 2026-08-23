using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;

namespace Orbit.Infrastructure.Tests.Persistence;

public class DailyAiQuotaByLocalDateMigrationTests
{
    [Fact]
    public void Up_ReplacesMonthlySchemaAndConfigsAndZeroesEveryCounter()
    {
        var operations = GetOperations("Up");

        operations.OfType<DropColumnOperation>().Should().ContainSingle()
            .Which.Name.Should().Be("AiMessagesResetAt");
        var rename = operations.OfType<RenameColumnOperation>().Should().ContainSingle().Subject;
        rename.Name.Should().Be("AiMessagesUsedThisMonth");
        rename.NewName.Should().Be("AiMessagesUsedToday");
        var localDate = operations.OfType<AddColumnOperation>().Should().ContainSingle().Subject;
        localDate.Name.Should().Be("AiMessagesLocalDate");
        localDate.ColumnType.Should().Be("date");
        localDate.IsNullable.Should().BeTrue();

        operations.OfType<SqlOperation>().Should().ContainSingle()
            .Which.Sql.Should().Be("UPDATE \"Users\" SET \"AiMessagesUsedToday\" = 0;");

        DeletedKeys(operations).Should().BeEquivalentTo(
            ["FreeAiMessagesPerMonth", "ProAiMessagesPerMonth"]);
        InsertedRows(operations).Should().BeEquivalentTo(
            [
                ("FreeAiMessagesPerDay", "Daily AI message limit for free plan users", "5"),
                ("ProAiMessagesPerDay", "Daily AI message limit for Pro plan users", "50")
            ]);
    }

    [Fact]
    public void Down_RestoresMonthlySchemaAndConfigs()
    {
        var operations = GetOperations("Down");

        operations.OfType<DropColumnOperation>().Should().ContainSingle()
            .Which.Name.Should().Be("AiMessagesLocalDate");
        var rename = operations.OfType<RenameColumnOperation>().Should().ContainSingle().Subject;
        rename.Name.Should().Be("AiMessagesUsedToday");
        rename.NewName.Should().Be("AiMessagesUsedThisMonth");
        var resetAt = operations.OfType<AddColumnOperation>().Should().ContainSingle().Subject;
        resetAt.Name.Should().Be("AiMessagesResetAt");
        resetAt.ColumnType.Should().Be("timestamp with time zone");
        resetAt.IsNullable.Should().BeTrue();

        DeletedKeys(operations).Should().BeEquivalentTo(
            ["FreeAiMessagesPerDay", "ProAiMessagesPerDay"]);
        InsertedRows(operations).Should().BeEquivalentTo(
            [
                ("FreeAiMessagesPerMonth", "Monthly AI message limit for free plan users", "20"),
                ("ProAiMessagesPerMonth", "Monthly AI message limit for Pro plan users", "500")
            ]);
    }

    private static IReadOnlyList<MigrationOperation> GetOperations(string methodName)
    {
        var migration = new DailyAiQuotaByLocalDate();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(DailyAiQuotaByLocalDate)
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);
        return builder.Operations;
    }

    private static IReadOnlyList<string> DeletedKeys(IEnumerable<MigrationOperation> operations) =>
        operations
            .OfType<DeleteDataOperation>()
            .Select(operation => (string)operation.KeyValues[0, 0]!)
            .ToList();

    private static IReadOnlyList<(string Key, string Description, string Value)> InsertedRows(
        IEnumerable<MigrationOperation> operations) =>
        operations
            .OfType<InsertDataOperation>()
            .Select(operation => (
                (string)operation.Values[0, 0]!,
                (string)operation.Values[0, 1]!,
                (string)operation.Values[0, 2]!))
            .ToList();
}
