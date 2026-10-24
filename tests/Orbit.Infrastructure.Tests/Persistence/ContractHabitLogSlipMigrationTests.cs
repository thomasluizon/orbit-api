using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class ContractHabitLogSlipMigrationTests
{
    [Fact]
    public void Up_PostgresBackfillsBeforeRequiringClassificationWithoutDefault()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var migration = new ContractHabitLogSlip { ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL" };
        var operations = migration.UpOperations.ToList();
        var column = operations.OfType<AlterColumnOperation>().Should().ContainSingle().Subject;
        column.Name.Should().Be("IsSlip");
        column.Table.Should().Be("HabitLogs");
        column.IsNullable.Should().BeFalse();
        column.OldColumn.IsNullable.Should().BeTrue();
        column.DefaultValue.Should().BeNull();
        column.DefaultValueSql.Should().BeNull();
        var backfill = operations.OfType<SqlOperation>().Should()
            .ContainSingle(operation => operation.Sql.Contains("UPDATE", StringComparison.Ordinal)).Subject;
        operations.IndexOf(backfill).Should().BeLessThan(operations.IndexOf(column));

        var script = context.GetService<IMigrator>().GenerateScript(
            "RecordDeletionStoppedRenewals", "ContractHabitLogSlip");
        script.Should().Contain("DROP TRIGGER IF EXISTS \"SetHabitLogIsSlipOnInsert\" ON \"HabitLogs\"")
            .And.Contain("DROP FUNCTION IF EXISTS \"SetHabitLogIsSlipOnInsert\"()")
            .And.Contain("ALTER TABLE \"HabitLogs\" ALTER COLUMN \"IsSlip\" SET NOT NULL")
            .And.NotContain("SET DEFAULT");
        script.IndexOf("UPDATE", StringComparison.Ordinal).Should()
            .BeLessThan(script.IndexOf("SET NOT NULL", StringComparison.Ordinal));
    }

    [Fact]
    public void Down_RestoresNullableColumnAndOldWriterTrigger()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);

        var script = context.GetService<IMigrator>().GenerateScript(
            "ContractHabitLogSlip", "RecordDeletionStoppedRenewals");

        script.Should().Contain("ALTER TABLE \"HabitLogs\" ALTER COLUMN \"IsSlip\" DROP NOT NULL")
            .And.Contain("CREATE FUNCTION \"SetHabitLogIsSlipOnInsert\"() RETURNS trigger")
            .And.Contain("BEFORE INSERT ON \"HabitLogs\"")
            .And.Contain("EXECUTE FUNCTION \"SetHabitLogIsSlipOnInsert\"()");
        script.IndexOf("DROP NOT NULL", StringComparison.Ordinal).Should()
            .BeLessThan(script.IndexOf("CREATE TRIGGER", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, 1, true)]
    [InlineData(false, 1, false)]
    [InlineData(true, 0, false)]
    [InlineData(true, -1, false)]
    public async Task Up_BackfillsNullRemovesTriggerAndEnforcesNoDefault(bool badHabit, int value, bool expected)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        await CreateExpandedSchemaAsync(context);
        var habit = CreateHabit(context, badHabit);
        var log = habit.Log(new DateOnly(2026, 1, 1)).Value;
        context.Habits.Add(habit);
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"HabitLogs\" SET \"Value\" = {value}, \"IsSlip\" = NULL, \"IsDeleted\" = 1 WHERE \"Id\" = {log.Id}");

        await ApplyAsync(context, new ContractHabitLogSlip());
        context.ChangeTracker.Clear();

        (await context.HabitLogs.IgnoreQueryFilters().SingleAsync()).IsSlip.Should().Be(expected);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name = 'SetHabitLogIsSlipOnInsert'";
        Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(0);
        var insertWithoutClassification = () => context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"HabitLogs\" (\"Id\", \"HabitId\", \"Date\", \"Value\", \"CreatedAtUtc\", \"UpdatedAtUtc\", \"IsDeleted\") VALUES ({Guid.NewGuid()}, {habit.Id}, {new DateOnly(2026, 1, 2)}, {1m}, {log.CreatedAtUtc}, {log.UpdatedAtUtc}, {false})");
        await insertWithoutClassification.Should().ThrowAsync<SqliteException>()
            .WithMessage("*NOT NULL constraint failed: HabitLogs.IsSlip*");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Up_PreservesExplicitHistoricalClassification(bool badHabit, bool recordedSlip)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        await CreateExpandedSchemaAsync(context);
        var habit = CreateHabit(context, badHabit);
        var log = habit.Log(new DateOnly(2026, 1, 1)).Value;
        context.Habits.Add(habit);
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"HabitLogs\" SET \"IsSlip\" = {recordedSlip} WHERE \"Id\" = {log.Id}");

        await ApplyAsync(context, new ContractHabitLogSlip());
        context.ChangeTracker.Clear();

        (await context.HabitLogs.SingleAsync()).IsSlip.Should().Be(recordedSlip);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NewBinary_ReadsAndWritesExpandedSchema(bool badHabit)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        await CreateExpandedSchemaAsync(context);
        var habit = CreateHabit(context, badHabit);
        var log = habit.Log(new DateOnly(2026, 1, 1)).Value;
        context.Habits.Add(habit);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        (await new HabitLogReader(context).ReadRecentLogsAsync(habit.Id, log.Date, 10))
            .Should().ContainSingle().Which.IsSlip.Should().Be(badHabit);
        var reloaded = await context.Habits.Include(h => h.Logs).SingleAsync();
        var nextLog = reloaded.Log(log.Date.AddDays(1));
        nextLog.IsSuccess.Should().BeTrue();
        context.HabitLogs.Add(nextLog.Value);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        (await context.HabitLogs.ToListAsync()).Should().HaveCount(2)
            .And.OnlyContain(item => item.IsSlip == badHabit);
        (await new HabitLogReader(context).GetLastCompletionDateAsync(habit.UserId))
            .Should().Be(badHabit ? null : log.Date.AddDays(1));
    }

    private static Habit CreateHabit(OrbitDbContext context, bool badHabit)
    {
        var user = User.Create("User", "user@example.com").Value;
        context.Users.Add(user);
        return Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            user.Id, "Habit", FrequencyUnit.Day, 1, new DateOnly(2026, 1, 1), IsBadHabit: badHabit)).Value;
    }

    private static async Task CreateExpandedSchemaAsync(OrbitDbContext context)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'HabitLogs'";
        var createTable = (string)(await command.ExecuteScalarAsync())!;
        createTable.Should().Contain("\"IsSlip\" INTEGER NOT NULL");
        await context.Database.ExecuteSqlRawAsync("DROP TABLE \"HabitLogs\"");
        await context.Database.ExecuteSqlRawAsync(createTable.Replace(
            "\"IsSlip\" INTEGER NOT NULL", "\"IsSlip\" INTEGER NULL", StringComparison.Ordinal));
        var expand = new RecordHabitLogSlip { ActiveProvider = "Microsoft.EntityFrameworkCore.Sqlite" };
        foreach (var operation in expand.UpOperations.OfType<SqlOperation>())
            await context.Database.ExecuteSqlRawAsync(operation.Sql);
    }

    private static async Task ApplyAsync(OrbitDbContext context, Migration migration)
    {
        migration.ActiveProvider = "Microsoft.EntityFrameworkCore.Sqlite";
        var model = context.GetService<IDesignTimeModel>().Model;
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, model);
        foreach (var command in commands)
            await context.Database.ExecuteSqlRawAsync(command.CommandText);
    }
}
