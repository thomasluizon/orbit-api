using FluentAssertions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class BoundedReadIndexMigrationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void LatestMigrationTarget_IncludesBothMergedModelsAndMatchesSnapshot()
    {
        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var migrations = db.GetService<IMigrationsAssembly>();
        migrations.Migrations.Keys.Where(id => id.EndsWith("_AddBoundedReadState", StringComparison.Ordinal)
            || id.EndsWith("_PersistOAuthClients", StringComparison.Ordinal)
            || id.EndsWith("_CompleteBoundedReadIndexes", StringComparison.Ordinal)).Should().Equal(
            "20261102113348_AddBoundedReadState",
            "20261102153013_PersistOAuthClients",
            "20261102193912_CompleteBoundedReadIndexes");
        var initializer = db.GetService<IModelRuntimeInitializer>();
        var target = initializer.Initialize(new AddPendingOperationExecutionResult().TargetModel, designTime: true);
        var snapshot = initializer.Initialize(new OrbitDbContextModelSnapshot().Model, designTime: true);

        target.FindEntityType("Orbit.Domain.Entities.OAuthClient").Should().NotBeNull();
        target.FindEntityType("Orbit.Domain.Entities.XpDailyTotal").Should().NotBeNull();
        target.FindEntityType("Orbit.Domain.Entities.EmailChallenge").Should().NotBeNull();
        target.FindEntityType("Orbit.Domain.Entities.MarketingDelivery").Should().NotBeNull();
        target.FindEntityType("Orbit.Domain.Entities.MarketingBroadcastDelivery").Should().BeNull();
        target.FindEntityType("Orbit.Domain.Entities.User")!.FindProperty("GoogleAccessTokenExpiresAtUtc").Should().NotBeNull();
        target.FindEntityType("Orbit.Domain.Entities.Habit")!.FindProperty("LongestRecordedStreak").Should().NotBeNull();
        target.FindEntityType("Orbit.Domain.Entities.Habit")!.FindProperty("MonthDays").Should().NotBeNull();
        target.FindEntityType("Orbit.Domain.Entities.PendingAgentOperationState")!.FindProperty("ExecutionJson").Should().NotBeNull();
        db.GetService<IMigrationsModelDiffer>().GetDifferences(target.GetRelationalModel(), snapshot.GetRelationalModel())
            .Should().BeEmpty();
        db.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Theory]
    [InlineData(typeof(RestoreBoundedReadIndexes), "20261102072150_RestoreBoundedReadIndexes")]
    [InlineData(typeof(AddBoundedReadState), "20261102113348_AddBoundedReadState")]
    [InlineData(typeof(CompleteBoundedReadIndexes), "20261102193912_CompleteBoundedReadIndexes")]
    public void UnappliedMigrations_MeetLockRulesWithoutExceptions(Type type, string id)
    {
        var migration = (Migration)(Activator.CreateInstance(type) ?? throw new InvalidOperationException());
        migration.ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
        var policy = new MigrationLockPolicy("20261101210800_PersistServerConversations", "applied in production before this gate", []);
        MigrationLockGate.Check(id, migration.UpOperations, policy).Should().BeEmpty();
    }

    [Fact]
    public void Migration_AddsReminderAndUserIndexesWithoutChangingTheUsageUniqueKey()
    {
        var migration = new RestoreBoundedReadIndexes();
        var reminderMigration = new RestoreReminderSchedulerIndexes();
        migration.UpOperations.Should().HaveCount(2).And.OnlyContain(operation => operation is SqlOperation || operation is CreateIndexOperation);
        var indexes = migration.UpOperations.Concat(reminderMigration.UpOperations).OfType<CreateIndexOperation>().ToDictionary(index => index.Name);
        indexes["IX_Habits_Reminder"].Columns.Should().Equal("ReminderEnabled", "IsCompleted", "Schedule");
        indexes["IX_Habits_Reminder"].Filter.Should().Be("\"ReminderEnabled\" = TRUE AND \"IsCompleted\" = FALSE");
        migration.UpOperations.OfType<SqlOperation>().Should().OnlyContain(sql => sql.SuppressTransaction);
        indexes["IX_AiUsageDaily_UserId"].Columns.Should().Equal("UserId");
        indexes.Values.Should().OnlyContain(index => !index.IsUnique);
        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        db.Database.HasPendingModelChanges().Should().BeFalse();
    }
    [Fact]
    public void MergedIndexMigrations_CreateEachNamedIndexOnce()
    {
        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var script = db.GetService<IMigrator>().GenerateScript("20261102071352_DropGeneralTaskCompletionBackfill",
            "20261102073314_RestoreReminderSchedulerIndexes");
        script.Split("CREATE INDEX CONCURRENTLY \"IX_AiUsageDaily_UserId\"", StringSplitOptions.None)
            .Should().HaveCount(2);
        script.Split("CREATE INDEX CONCURRENTLY \"IX_Habits_Reminder\"", StringSplitOptions.None)
            .Should().HaveCount(2);
        new RestoreBoundedReadIndexes().DownOperations.OfType<SqlOperation>()
            .Should().NotContain(sql => sql.Sql.Contains("IX_Habits_Reminder", StringComparison.Ordinal));
    }

    [Fact]
    public void SchemaExpansion_IsRecordedAtomicallyBeforeConcurrentIndexWork()
    {
        var migration = new AddBoundedReadState();
        migration.UpOperations.OfType<SqlOperation>().Should().OnlyContain(sql => !sql.SuppressTransaction);
        migration.DownOperations.OfType<SqlOperation>().Should().OnlyContain(sql => !sql.SuppressTransaction);
        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var script = db.GetService<IMigrator>().GenerateScript("20261102102120_PersistStripeCustomerErasureRecovery",
            "20261102193912_CompleteBoundedReadIndexes");
        var history = script.IndexOf("VALUES ('20261102113348_AddBoundedReadState'", StringComparison.Ordinal);
        history.Should().BePositive();
        foreach (var index in new CompleteBoundedReadIndexes().UpOperations.OfType<CreateIndexOperation>())
        {
            var build = script.IndexOf($"CREATE INDEX CONCURRENTLY \"{index.Name}\"", StringComparison.Ordinal);
            build.Should().BeGreaterThan(history);
            script[history..build].Should().Contain("COMMIT;");
        }
    }

    [Theory]
    [InlineData(typeof(RestoreBoundedReadIndexes))]
    [InlineData(typeof(CompleteBoundedReadIndexes))]
    public void ConcurrentBuilds_AlwaysDropTheSameIndexBeforeCreatingIt(Type type)
    {
        var migration = (Migration)(Activator.CreateInstance(type) ?? throw new InvalidOperationException());
        var operations = migration.UpOperations;
        for (var position = 0; position < operations.Count; position++)
        {
            if (operations[position] is not CreateIndexOperation index) continue;
            position.Should().BePositive();
            var drop = operations[position - 1].Should().BeOfType<SqlOperation>().Subject;
            drop.SuppressTransaction.Should().BeTrue();
            drop.Sql.Should().Contain($"DROP INDEX CONCURRENTLY IF EXISTS \"{index.Name}\";");
            index["Npgsql:CreatedConcurrently"].Should().Be(true);
        }
    }

    [Fact]
    public void BoundedSchemaAndIndexes_FollowTheMainSchedulerChangesToTheirTables()
    {
        var migrations = new[] { typeof(Orbit.Infrastructure.Migrations.ScheduledJobIndexes), typeof(AddBoundedReadState), typeof(CompleteBoundedReadIndexes) };
        migrations.Select(type => type.GetCustomAttribute<MigrationAttribute>()!.Id)
            .Should().BeInAscendingOrder(StringComparer.Ordinal);
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
