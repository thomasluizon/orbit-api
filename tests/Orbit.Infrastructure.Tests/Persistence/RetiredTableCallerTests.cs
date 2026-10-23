using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Tests.Services;
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
        var user = await SeedLiveRows(factory.Context);
        commands.Sql.Clear();

        await new AccountResetRepository(factory.Context, Substitute.For<IAccountEventCollector>())
            .DeleteAllUserDataAsync(user.Id);

        AssertNoRetiredCommands(commands);
        (await factory.Context.Users.AnyAsync(u => u.Id == user.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Export_PreservesEmptySocialFieldsWithoutReadingRetiredTables()
    {
        var commands = new CommandCapture();
        using var factory = new SqliteOrbitDbContextFactory(commands);
        var user = await SeedLiveRows(factory.Context);
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

    [Fact]
    public void CompatibilityMigration_OnlyChangesForeignKeys()
    {
        var migration = new CascadeRetiredFeatureParents { ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL" };
        migration.UpOperations.Should().NotBeEmpty();
        migration.UpOperations.Should().OnlyContain(operation =>
            operation is DropForeignKeyOperation || operation is SqlOperation);
        migration.UpOperations.OfType<SqlOperation>().Should().HaveCount(13)
            .And.OnlyContain(operation => operation.Sql.EndsWith("ON DELETE CASCADE NOT VALID;", StringComparison.Ordinal));
    }

    internal static int AssertCompatibilityForeignKeys(IReadOnlyList<MigrationOperation> operations)
    {
        var expected = CascadeRetiredFeatureParentsMigrationTests.ForeignKeys.Select(key => new
        {
            Name = $"FK_{key.Table}_{key.PrincipalTable}_{key.Column}",
            key.Table,
            Columns = new[] { key.Column },
            key.PrincipalTable,
            PrincipalColumns = new[] { "Id" },
            OnDelete = ReferentialAction.Cascade
        }).ToArray();
        var names = expected.Select(key => key.Name).ToHashSet();
        var compatibility = operations.Where(operation =>
            operation is AddForeignKeyOperation add && names.Contains(add.Name)
            || operation is DropForeignKeyOperation drop && names.Contains(drop.Name)).ToArray();
        compatibility.OfType<DropForeignKeyOperation>().Select(key => new { key.Name, key.Table }).Should()
            .BeEquivalentTo(expected.Where(key => key.Table != "UserFacts").Select(key => new { key.Name, key.Table }));
        compatibility.OfType<AddForeignKeyOperation>()
            .Select(key => new { key.Name, key.Table, key.Columns, key.PrincipalTable, key.PrincipalColumns, key.OnDelete }).Should()
            .BeEquivalentTo(expected);
        return expected.Length + expected.Count(key => key.Table != "UserFacts");
    }

    [Fact]
    public async Task PermanentDeletion_DoesNotIssueCommandsAgainstRetiredTables()
    {
        var commands = new CommandCapture();
        using var factory = new SqliteOrbitDbContextFactory(commands);
        var db = factory.Context;
        var user = await SeedLiveRows(db);
        user.Deactivate(DateTime.UtcNow.AddDays(-1));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        using var provider = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton<IObjectStorageDeletionService>(new FakeObjectStore())
            .AddSingleton<IUnitOfWork>(new UnitOfWork(db, new DatabaseConnectionSettings()))
            .AddSingleton<IAccountResetRepository>(new AccountResetRepository(db, Substitute.For<IAccountEventCollector>()))
            .AddSingleton<IGenericRepository<MarketingContact>>(new GenericRepository<MarketingContact>(db))
            .BuildServiceProvider();
        var logger = Substitute.For<ILogger<AccountDeletionService>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        using var service = new AccountDeletionService(provider.GetRequiredService<IServiceScopeFactory>(),
            logger, new ConfigurationBuilder().Build());
        commands.Sql.Clear();

        await service.RunAsync(CancellationToken.None);

        logger.ReceivedCalls().SelectMany(call => call.GetArguments()).OfType<Exception>().Should().BeEmpty();
        AssertNoRetiredCommands(commands);
        (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == user.Id)).Should().BeFalse();
        (await db.Habits.IgnoreQueryFilters().AnyAsync(h => h.UserId == user.Id)).Should().BeFalse();
        (await db.Users.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task BackgroundMaintenance_DoesNotReadOrWriteRetiredTables()
    {
        var commands = new CommandCapture();
        using var factory = new SqliteOrbitDbContextFactory(commands);
        await SeedLiveRows(factory.Context);
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

    private static async Task<User> SeedLiveRows(OrbitDbContext db)
    {
        var user = User.Create("Target", "target@example.com").Value;
        var other = User.Create("Other", "other@example.com").Value;
        db.Users.AddRange(user, other);
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Live habit", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 6, 15))).Value;
        db.Habits.Add(habit);
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
