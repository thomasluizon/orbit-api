using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Notifications.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// The effects of the habit commands inside one open transaction, as a change set runs them, on real PostgreSQL.
/// A failing effect is a real failed INSERT: a trigger on "XpAwardLogs" raises SQLSTATE 23505.
/// </summary>
public sealed partial class HabitEffectPostgresTests
{
    private readonly StatementFaultInterceptor _faults = new();
    private readonly FailAfterWriteBehavior _failingUpdate = new();
    private readonly IHabitEmojiInferenceService _emojiInference = Substitute.For<IHabitEmojiInferenceService>();

    [PostgresFact]
    public async Task CreateInsideTransaction_SwallowedEffectFailure_DoesNotAbortLaterStatements()
    {
        await using var app = await StartAsync();
        var user = await SeedUserAsync(app);
        await FailXpAwardsAsync(app, times: 1000);

        var result = await InTransactionAsync(app, async (scope, ct) =>
        {
            var created = await Tool(scope, "create_habit").ExecuteAsync(
                JsonSerializer.SerializeToElement(new { title = "Read", frequency_unit = "Day" }), user.Id, ct);
            var count = await scope.ServiceProvider.GetRequiredService<OrbitDbContext>().Habits.CountAsync(ct);
            return (created, count);
        });

        result.created.Success.Should().BeTrue(result.created.Error);
        result.count.Should().Be(1);
        (await app.ReadAsync(db => db.Habits.CountAsync())).Should().Be(1);
        (await app.ReadAsync(db => db.XpAwardLogs.CountAsync())).Should().Be(0);
    }

    [PostgresFact]
    public async Task CreateInsideTransaction_FailedEffectQuery_DoesNotAbortTheCreate()
    {
        await using var app = await StartAsync();
        var user = await SeedUserAsync(app);
        _faults.FailReadsOf("UserAchievements");

        var result = await InTransactionAsync(app, (scope, ct) => Tool(scope, "create_habit").ExecuteAsync(
            JsonSerializer.SerializeToElement(new { title = "Read", frequency_unit = "Day" }), user.Id, ct));

        result.Success.Should().BeTrue(result.Error);
        _faults.FailedReads.Should().BePositive();
        (await app.ReadAsync(db => db.Habits.CountAsync())).Should().Be(1);
    }

    [PostgresFact]
    public async Task LogInsideTransaction_FailedEffectQuery_KeepsTheLogAndTheStreak()
    {
        await using var app = await StartAsync();
        var user = await SeedUserAsync(app);
        var habit = await SeedHabitAsync(app, user.Id, "Run");
        _faults.FailReadsOf("UserAchievements");

        var result = await InTransactionAsync(app, (scope, ct) => Tool(scope, "log_habit").ExecuteAsync(
            JsonSerializer.SerializeToElement(new { habit_id = habit.Id }), user.Id, ct));

        result.Success.Should().BeTrue(result.Error);
        _faults.FailedReads.Should().BePositive();
        (await app.ReadAsync(db => db.HabitLogs.CountAsync())).Should().Be(1);
        (await app.ReadAsync(db => db.Users.SingleAsync(saved => saved.Id == user.Id))).CurrentStreak.Should().Be(1);
    }

    [PostgresFact]
    public async Task DuplicateLogInsideTransaction_ReturnsTypedFailureAndLaterStatementsRun()
    {
        await using var app = await StartAsync();
        var user = await SeedUserAsync(app);
        var habit = await SeedHabitAsync(app, user.Id, "Run", loggedDaysAgo: [4]);
        await ArmConcurrentLogAsync(app, habit.Id, await TodayAsync(app, user.Id));

        var result = await InTransactionAsync(app, async (scope, ct) =>
        {
            var logged = await Tool(scope, "log_habit").ExecuteAsync(
                JsonSerializer.SerializeToElement(new { habit_id = habit.Id }), user.Id, ct);
            var count = await scope.ServiceProvider.GetRequiredService<OrbitDbContext>().Habits.CountAsync(ct);
            return (logged, count);
        });

        _faults.Fired.Should().BeTrue();
        result.logged.Success.Should().BeFalse();
        result.logged.ErrorCode.Should().Be(DomainErrors.AlreadyLoggedForDate.Code);
        result.count.Should().Be(1);
        (await app.ReadAsync(db => db.HabitLogs.CountAsync())).Should().Be(1);
    }

    [PostgresFact]
    public async Task UpdateWithGoalIdsInsideTransaction_RetriesTheGoalRewardConflict()
    {
        await using var app = await StartAsync();
        var user = await SeedUserAsync(app);
        var goal = await SeedGoalAsync(app, user.Id, GoalType.Standard);
        var habit = await SeedHabitAsync(app, user.Id, "Run", loggedDaysAgo: [0]);
        await FailXpAwardsAsync(app, times: 1);

        var result = await InTransactionAsync(app, (scope, ct) => Tool(scope, "update_habit").ExecuteAsync(
            JsonSerializer.SerializeToElement(new { habit_id = habit.Id, goal_ids = new[] { goal.Id } }), user.Id, ct));

        result.Success.Should().BeTrue(result.Error);
        await AssertGoalRewardedAsync(app, goal.Id);
    }

    [PostgresFact]
    public async Task SkipWithActiveStreakGoalInsideTransaction_RetriesTheGoalRewardConflict()
    {
        await using var app = await StartAsync();
        var user = await SeedUserAsync(app);
        var goal = await SeedGoalAsync(app, user.Id, GoalType.Streak);
        var habit = await SeedHabitAsync(app, user.Id, "Run", loggedDaysAgo: [1], goalId: goal.Id);
        await FailXpAwardsAsync(app, times: 1);

        var result = await InTransactionAsync(app, (scope, ct) => Tool(scope, "skip_habit").ExecuteAsync(
            JsonSerializer.SerializeToElement(new { habit_id = habit.Id }), user.Id, ct));

        result.Success.Should().BeTrue(result.Error);
        await AssertGoalRewardedAsync(app, goal.Id);
    }

    [PostgresFact]
    public async Task GoalLinkInsideTransaction_RetriesTheGoalRewardConflict()
    {
        await using var app = await StartAsync();
        var user = await SeedUserAsync(app);
        var goal = await SeedGoalAsync(app, user.Id, GoalType.Standard);
        var habit = await SeedHabitAsync(app, user.Id, "Run", loggedDaysAgo: [0]);
        await FailXpAwardsAsync(app, times: 1);

        var result = await InTransactionAsync(app, (scope, ct) => Tool(scope, "link_goals_to_habit").ExecuteAsync(
            JsonSerializer.SerializeToElement(new { habit_id = habit.Id, goal_ids = new[] { goal.Id } }), user.Id, ct));

        result.Success.Should().BeTrue(result.Error);
        await AssertGoalRewardedAsync(app, goal.Id);
    }

    private Task<PostgresOrbitApp> StartAsync() => PostgresOrbitApp.StartAsync(services =>
    {
        services.ConfigureDbContext<OrbitDbContext>(options => options.AddInterceptors(_faults, _saves));
        services.AddSingleton<IPipelineBehavior<UpdateHabitFieldsCommand, Result<string>>>(_failingUpdate);
        services.AddSingleton<IPipelineBehavior<DeleteNotificationCommand, Result>>(_failingDeletion);
        services.AddScoped<IPushNotificationService>(provider => new TransactionRecordingPush(
            provider.GetRequiredService<IUnitOfWork>(), _pushTransactions));
        services.AddScoped<IHabitEmojiInferenceService>(provider => new TransactionRecordingInference(
            _emojiInference, provider.GetRequiredService<IUnitOfWork>(), _inferenceTransactions));
    });

    private static async Task<T> InTransactionAsync<T>(PostgresOrbitApp app,
        Func<AsyncServiceScope, CancellationToken, Task<T>> operation)
    {
        await using var scope = app.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .ExecuteInTransactionAsync(ct => operation(scope, ct));
    }

    private static IAiTool Tool(AsyncServiceScope scope, string name) =>
        scope.ServiceProvider.GetRequiredService<AiToolRegistry>().GetTool(name)!;

    private static async Task AssertGoalRewardedAsync(PostgresOrbitApp app, Guid goalId)
    {
        (await app.ReadAsync(db => db.Goals.SingleAsync(goal => goal.Id == goalId))).Status.Should().Be(GoalStatus.Completed);
        (await app.ReadAsync(db => db.XpAwardLogs.CountAsync(log => log.SourceId == goalId))).Should().Be(1);
    }

    private static async Task<User> SeedUserAsync(PostgresOrbitApp app)
    {
        await using var scope = app.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var user = User.Create("Alex", $"alex-{Guid.NewGuid():N}@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(7));
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<DateOnly> TodayAsync(PostgresOrbitApp app, Guid userId)
    {
        await using var scope = app.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUserDateService>().GetUserTodayAsync(userId);
    }

    private static async Task<Habit> SeedHabitAsync(PostgresOrbitApp app, Guid userId, string title,
        int[]? loggedDaysAgo = null, Guid? goalId = null)
    {
        var today = await TodayAsync(app, userId);
        await using var scope = app.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, userId, title, FrequencyUnit.Day, 1,
            today.AddDays(-5))).Value;
        db.Habits.Add(habit);
        foreach (var daysAgo in (loggedDaysAgo ?? []).OrderDescending())
            db.HabitLogs.Add(habit.Log(today.AddDays(-daysAgo), advanceDueDate: false).Value);
        if (goalId is { } id)
            habit.AddGoal(await db.Goals.SingleAsync(goal => goal.Id == id));
        await db.SaveChangesAsync();
        return habit;
    }

    private static async Task<Goal> SeedGoalAsync(PostgresOrbitApp app, Guid userId, GoalType type)
    {
        await using var scope = app.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var goal = Goal.Create(new Goal.CreateGoalParams(userId, "Finish", 1, "times", Type: type)).Value;
        db.Goals.Add(goal);
        await db.SaveChangesAsync();
        return goal;
    }

    /// <summary>
    /// Before the command inserts its log, another connection moves the habit's only earlier log to
    /// <paramref name="date"/>. The command's insert then meets a real unique violation of a committed row.
    /// </summary>
    private async Task ArmConcurrentLogAsync(PostgresOrbitApp app, Guid habitId, DateOnly date)
    {
        var logId = await app.ReadAsync(db => db.HabitLogs.Where(log => log.HabitId == habitId).Select(log => log.Id).SingleAsync());
        _faults.Arm(() => app.ExecuteSqlAsync(
            $"UPDATE \"HabitLogs\" SET \"Date\" = DATE '{date:yyyy-MM-dd}' WHERE \"Id\" = '{logId}'"));
    }

    private static Task FailXpAwardsAsync(PostgresOrbitApp app, int times) => app.ExecuteSqlAsync($$"""
        CREATE SEQUENCE orbit_test_xp_failures;
        CREATE FUNCTION orbit_test_fail_xp() RETURNS trigger LANGUAGE plpgsql AS $body$
        BEGIN
            IF nextval('orbit_test_xp_failures') <= {{times}} THEN
                RAISE EXCEPTION 'Simulated reward conflict' USING ERRCODE = 'unique_violation';
            END IF;
            RETURN NEW;
        END
        $body$;
        CREATE TRIGGER orbit_test_fail_xp BEFORE INSERT ON "XpAwardLogs"
            FOR EACH ROW EXECUTE FUNCTION orbit_test_fail_xp();
        """);

    /// <summary>
    /// Runs a statement on another connection just before the command inserts a row of one table. It can also send
    /// each read of one table to the server as a statement that fails, as a query that times out does.
    /// </summary>
    private sealed class StatementFaultInterceptor : DbCommandInterceptor
    {
        private Func<Task>? _beforeInsert;
        private string _insertTable = "HabitLogs";
        private string? _failedTable;
        private int _failedReads;

        public void Arm(Func<Task> beforeInsert, string table = "HabitLogs")
        {
            _insertTable = table;
            _beforeInsert = beforeInsert;
        }
        public void FailReadsOf(string table) => _failedTable = table;
        public bool Fired { get; private set; }
        public int FailedReads => _failedReads;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (_failedTable is { } table && command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains($"FROM \"{table}\"", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _failedReads);
                command.CommandText = "SELECT 1 / 0";
                command.Parameters.Clear();
            }
            if (command.CommandText.Contains($"INSERT INTO \"{_insertTable}\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _beforeInsert, null) is { } insert)
            {
                Fired = true;
                await insert();
            }
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
