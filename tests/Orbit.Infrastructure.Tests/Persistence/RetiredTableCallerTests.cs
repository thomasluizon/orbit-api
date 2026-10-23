using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Goals.Services;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Events;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public class RetiredTableCallerTests
{
    private static readonly string[] Tables =
    [
        "Challenges", "ChallengeParticipants", "ChallengeParticipantHabits",
        "AccountabilityPairs", "AccountabilityPairHabits", "AccountabilityCheckIns",
        "Friendships", "FriendFeedEvents", "Cheers", "BlockedUsers", "Reports",
        "UserFacts", "AiFactExtractionBatches"
    ];

    [Fact]
    public async Task Reset_DoesNotIssueCommandsAgainstRetiredTables()
    {
        var commands = new CommandCapture();
        using var factory = new SqliteOrbitDbContextFactory(commands);
        var user = await SeedRetiredRows(factory.Context);
        commands.Sql.Clear();

        await new AccountResetRepository(factory.Context, Substitute.For<IAccountEventCollector>())
            .DeleteAllUserDataAsync(user.Id);

        AssertNoRetiredCommands(commands);
        (await factory.Context.Users.AnyAsync(u => u.Id == user.Id)).Should().BeTrue();
        (await factory.Context.UserFacts.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        (await factory.Context.Friendships.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Export_PreservesEmptySocialFieldsWithoutReadingRetiredTables()
    {
        var commands = new CommandCapture();
        using var factory = new SqliteOrbitDbContextFactory(commands);
        var user = await SeedRetiredRows(factory.Context);
        var db = factory.Context;
        var repos = new ExportUserDataRepositories(
            new GenericRepository<User>(db), new GenericRepository<Habit>(db),
            new GenericRepository<HabitLog>(db), new GenericRepository<Goal>(db),
            new GenericRepository<GoalProgressLog>(db), new GenericRepository<Tag>(db),
            new GenericRepository<Notification>(db), new GenericRepository<ChecklistTemplate>(db),
            new GenericRepository<UserAchievement>(db), new GenericRepository<StreakFreeze>(db),
            new GenericRepository<Referral>(db), new GenericRepository<ApiKey>(db),
            new GenericRepository<ProactiveItem>(db),
            new GenericRepository<NotionConnection>(db), new GenericRepository<NotionTask>(db),
            new GenericRepository<ConversationTurn>(db));
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 6, 15));
        var progress = Substitute.For<IGoalProgressReadSyncer>();
        progress.ComputeFreshValuesAsync(user.Id, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        commands.Sql.Clear();

        var result = await new ExportUserDataQueryHandler(repos, dates, progress)
            .Handle(new ExportUserDataQuery(user.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        AssertNoRetiredCommands(commands);
        result.Value.Friendships.Should().BeEmpty();
        result.Value.Cheers.Should().BeEmpty();
        result.Value.BlockedUsers.Should().BeEmpty();
        result.Value.Reports.Should().BeEmpty();
        result.Value.FriendFeedEvents.Should().BeEmpty();
        result.Value.Habits.Should().ContainSingle();
        result.Value.Account.Email.Should().Be(user.Email);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteUser_CascadesEveryRetiredTable(bool reverseRoles)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = await SeedRetiredRows(factory.Context, reverseRoles);
        var db = factory.Context;
        db.ChangeTracker.Clear();

        await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync();

        (await db.Users.CountAsync()).Should().Be(1);
        foreach (var table in Tables)
        {
            var sql = $"SELECT COUNT(*) AS Value FROM \"{table}\"";
            var count = await db.Database.SqlQueryRaw<int>(sql).SingleAsync();
            count.Should().Be(0, "{0} must cascade from its live parent", table);
        }
    }

    [Fact]
    public void CompatibilityMigration_OnlyChangesForeignKeysAndNeedsNoBarrier()
    {
        var migration = new CascadeRetiredFeatureParents { ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL" };
        migration.UpOperations.Should().NotBeEmpty();
        migration.UpOperations.Should().OnlyContain(operation =>
            operation is DropForeignKeyOperation || operation is AddForeignKeyOperation);
        migration.UpOperations.OfType<AddForeignKeyOperation>().Should()
            .OnlyContain(foreignKey => foreignKey.OnDelete == ReferentialAction.Cascade);
        RollingMigrationGate.CheckRelease([("compatibility", migration.UpOperations)]).Should().BeEmpty();
    }

    [Fact]
    public async Task BackgroundMaintenance_DoesNotReadOrWriteRetiredTables()
    {
        var commands = new CommandCapture();
        using var factory = new SqliteOrbitDbContextFactory(commands);
        await SeedRetiredRows(factory.Context);
        using var provider = new ServiceCollection().AddSingleton(factory.Context).BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var encryption = Substitute.For<IEncryptionService>();
        encryption.IsConfigured.Returns(true);
        commands.Sql.Clear();

        await new SyncCleanupService(scopes, NullLogger<SyncCleanupService>.Instance)
            .PurgeSoftDeletedEntities(CancellationToken.None);
        using var migration = new DataEncryptionMigrationService(scopes, encryption,
            NullLogger<DataEncryptionMigrationService>.Instance);
        await migration.RunMigrationAsync(CancellationToken.None);

        AssertNoRetiredCommands(commands);
        (await factory.Context.AppConfigs.AnyAsync(c => c.Key == "EncryptionMigrationComplete_v2")).Should().BeTrue();
    }

    private static void AssertNoRetiredCommands(CommandCapture commands)
    {
        commands.Sql.Should().NotBeEmpty();
        foreach (var table in Tables)
            commands.Sql.Should().NotContain(sql => sql.Contains($"\"{table}\"", StringComparison.Ordinal));
    }

    private static async Task<User> SeedRetiredRows(OrbitDbContext db, bool reverseRoles = false)
    {
        var user = User.Create("Target", "target@example.com").Value;
        var other = User.Create("Other", "other@example.com").Value;
        db.Users.AddRange(user, other);
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Live habit", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 6, 15))).Value;
        db.Habits.Add(habit);
        var first = reverseRoles ? other.Id : user.Id;
        var second = reverseRoles ? user.Id : other.Id;
        db.Friendships.Add(Friendship.Create(first, second).Value);
        db.Cheers.Add(Cheer.Create(first, second, habit.Id, "Nice").Value);
        db.BlockedUsers.Add(BlockedUser.Create(first, second).Value);
        db.Reports.Add(Report.Create(first, second, ReportReason.Spam, "Report", null).Value);
        db.FriendFeedEvents.Add(FriendFeedEvent.StreakMilestone(user.Id, 7));
        var pair = AccountabilityPair.Create(first, second, AccountabilityCadence.Daily).Value;
        db.AccountabilityPairs.Add(pair);
        db.AccountabilityPairHabits.Add(AccountabilityPairHabit.Create(pair.Id, user.Id, habit.Id));
        db.AccountabilityCheckIns.Add(AccountabilityCheckIn.Create(pair.Id, user.Id, new DateOnly(2026, 6, 15), null).Value);
        var challenge = Challenge.Create(new CreateChallengeParams(user.Id, ChallengeType.CoopGoal,
            "Retired challenge", null, 10, new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 30), "retired")).Value;
        challenge.AddParticipant(user.Id, [habit.Id]);
        challenge.SoftDelete();
        db.Challenges.Add(challenge);
        var fact = UserFact.Create(user.Id, "Morning person", "preference").Value;
        fact.SoftDelete();
        db.UserFacts.Add(fact);
        db.AiFactExtractionBatches.Add(AiFactExtractionBatch.Create(user.Id, "batch", "input"));
        await db.SaveChangesAsync();
        return user;
    }

    private sealed class CommandCapture : DbCommandInterceptor
    {
        internal List<string> Sql { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
