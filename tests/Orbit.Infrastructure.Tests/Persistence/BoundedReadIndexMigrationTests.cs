using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class BoundedReadIndexMigrationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Migration_AddsReminderAndUserIndexesWithoutChangingTheUsageUniqueKey()
    {
        var migration = new RestoreBoundedReadIndexes();
        var reminderMigration = new RestoreReminderSchedulerIndexes();
        migration.UpOperations.Should().HaveCount(1).And.OnlyContain(operation => operation is CreateIndexOperation);
        var indexes = migration.UpOperations.Concat(reminderMigration.UpOperations).Cast<CreateIndexOperation>().ToDictionary(index => index.Name);
        indexes["IX_Habits_Reminder"].Columns.Should().Equal("ReminderEnabled", "IsCompleted", "Schedule");
        indexes["IX_Habits_Reminder"].Filter.Should().Be("\"ReminderEnabled\" = TRUE AND \"IsCompleted\" = FALSE");
        indexes["IX_AiUsageDaily_UserId"].Columns.Should().Equal("UserId");
        indexes.Values.Should().OnlyContain(index => !index.IsUnique);
        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        db.Database.HasPendingModelChanges().Should().BeFalse();
    }
    [Fact]
    public void MergedIndexMigrations_CreateEachNamedIndexOnce()
    {
        var indexes = new RestoreBoundedReadIndexes().UpOperations
            .Concat(new RestoreReminderSchedulerIndexes().UpOperations)
            .OfType<CreateIndexOperation>().ToArray();
        indexes.Select(index => index.Name).Should().OnlyHaveUniqueItems();
        indexes.Single(index => index.Name == "IX_Habits_Reminder").Columns
            .Should().Equal("ReminderEnabled", "IsCompleted", "Schedule");
        new RestoreBoundedReadIndexes().DownOperations.OfType<DropIndexOperation>()
            .Should().NotContain(index => index.Name == "IX_Habits_Reminder");
    }

    [PostgresFact]
    public Task ReminderIndex_IsUsableForTheSchedulerPredicate() => PostgresTestDatabase.WithDatabase(async db =>
    {
        var user = Orbit.Domain.Entities.User.Create("Index", "reminder-index@example.test").Value;
        db.Users.Add(user);
        for (var index = 0; index < 1000; index++)
            db.Habits.Add(Orbit.Domain.Entities.Habit.Create(new(Orbit.Domain.Enums.HabitSchedule.Recurring,
                user.Id, "Reminder", Orbit.Domain.Enums.FrequencyUnit.Day, 1, new DateOnly(2026, 6, 1), ReminderEnabled: index == 0)).Value);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_Habits_Reminder\"");
        await db.Database.ExecuteSqlRawAsync("ANALYZE \"Habits\"");
        const string sql = """
            EXPLAIN SELECT "Id", "NextDueDate" FROM "Habits"
            WHERE "ReminderEnabled" AND NOT "IsCompleted" AND "Schedule" = 2 AND "NextDueDate" <= DATE '2026-06-15'
            """;
        var before = string.Join("\n", await db.Database.SqlQueryRaw<string>(sql).ToListAsync());
        before.Should().NotContain("IX_Habits_Reminder");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_Habits_Reminder" ON "Habits" ("ReminderEnabled", "IsCompleted")
            WHERE "ReminderEnabled" AND NOT "IsCompleted"
            """);
        var after = string.Join("\n", await db.Database.SqlQueryRaw<string>(sql).ToListAsync());
        after.Should().Contain("IX_Habits_Reminder");
        output.WriteLine("Before:\n" + before + "\nAfter:\n" + after);
    });

}
