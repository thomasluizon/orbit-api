using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class WeeklyAiAllowanceMigrationTests
{
    [Fact]
    public void Migration_SeedsWeeklyLimitsAndRetiresDailyProKeys()
    {
        var migration = new AddProWeeklyAiAllowance { ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL" };
        SeedRows(migration.UpOperations).Should().BeEquivalentTo(new Dictionary<string, (string, string)>
        {
            ["ProAiMessagesPerWeek"] = ("Weekly AI message limit for Pro users", "250"),
            ["ProAiCallsPerWeek"] = ("Weekly AI call safety budget for Pro users", "2250"),
            ["ProAiMicroDollarsPerWeek"] = ("Weekly AI safety budget for Pro users in USD micro-dollars", "1750000")
        });
        migration.UpOperations.OfType<DeleteDataOperation>().Select(change => change.KeyValues[0, 0])
            .Should().BeEquivalentTo(new[] { "ProAiMessagesPerDay", "ProAiCallsPerDay", "ProAiMicroDollarsPerDay" });
        migration.UpOperations.OfType<UpdateDataOperation>().Should().BeEmpty();

        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var commands = generator.Generate(migration.UpOperations, context.GetService<IDesignTimeModel>().Model);
        var sql = string.Join("\n", commands.Select(command => command.CommandText));
        sql.Should().Contain("EXTRACT(DOW FROM \"AiMessagesLocalDate\")");
        sql.Should().Contain("\"WeekStartDay\"");
        sql.Should().Contain("\"AiMessagesUsedThisWeek\" = \"AiMessagesUsedToday\"");
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public void Rollback_RestoresDailyProRowsAndDescriptions()
    {
        var migration = new AddProWeeklyAiAllowance();

        SeedRows(migration.DownOperations).Should().BeEquivalentTo(new Dictionary<string, (string, string)>
        {
            ["ProAiMessagesPerDay"] = ("Daily AI message limit for Pro plan users", "50"),
            ["ProAiCallsPerDay"] = ("Daily AI call safety budget for Pro users", "450"),
            ["ProAiMicroDollarsPerDay"] = ("Daily AI safety budget for Pro users in USD micro-dollars", "120000")
        });
        migration.DownOperations.OfType<DeleteDataOperation>().Select(change => change.KeyValues[0, 0])
            .Should().BeEquivalentTo(new[] { "ProAiMessagesPerWeek", "ProAiCallsPerWeek", "ProAiMicroDollarsPerWeek" });
    }

    private static Dictionary<string, (string Description, string Value)> SeedRows(IReadOnlyList<MigrationOperation> operations) =>
        operations.OfType<InsertDataOperation>().SelectMany(seed =>
        {
            seed.Table.Should().Be("AppConfigs");
            seed.Columns.Should().Equal("Key", "Description", "Value");
            return Enumerable.Range(0, seed.Values.GetLength(0)).Select(row =>
                new KeyValuePair<string, (string, string)>(seed.Values[row, 0].Should().BeOfType<string>().Subject,
                    (seed.Values[row, 1].Should().BeOfType<string>().Subject,
                        seed.Values[row, 2].Should().BeOfType<string>().Subject)));
        }).ToDictionary();
}
