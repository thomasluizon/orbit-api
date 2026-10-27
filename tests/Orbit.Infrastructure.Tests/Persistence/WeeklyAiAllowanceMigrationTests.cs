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
    public void Migration_SeedsWeeklyLimitRetiresDailyKeyAndKeepsSafetyCapValues()
    {
        var migration = new AddProWeeklyAiAllowance { ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL" };
        var seed = migration.UpOperations.OfType<InsertDataOperation>().Single();
        seed.Table.Should().Be("AppConfigs");
        seed.Columns.Should().Equal("Key", "Description", "Value");
        seed.Values[0, 0].Should().Be("ProAiMessagesPerWeek");
        seed.Values[0, 2].Should().Be("250");
        migration.UpOperations.OfType<DeleteDataOperation>().Single().KeyValues[0, 0]
            .Should().Be("ProAiMessagesPerDay");
        migration.UpOperations.OfType<UpdateDataOperation>().Should().OnlyContain(change =>
            change.Columns.Length == 1 && change.Columns[0] == "Description");

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
}
