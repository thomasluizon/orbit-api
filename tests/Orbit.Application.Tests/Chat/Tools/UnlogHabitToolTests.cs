using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Goals.Services;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Application.Tests.Chat.Tools;

public sealed class UnlogHabitToolTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 8, 6);
    private readonly IUserDateService _dates = Substitute.For<IUserDateService>();
    private readonly IUnitOfWork _work = Substitute.For<IUnitOfWork>();
    private readonly IUserStreakService _streak = Substitute.For<IUserStreakService>();
    private readonly IGoalCompletionService _goals = Substitute.For<IGoalCompletionService>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();

    public UnlogHabitToolTests()
    {
        _dates.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _work.PassThroughTransactions<Result<ToolResult>>();
        _payGate.CanCreateHabits(UserId, 1, Arg.Any<CancellationToken>()).Returns(Result.Success());
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, -2)]
    [InlineData(true, 1)]
    public async Task PersistedLog_FreshContext_SoftDeletesWithoutAddingRows(bool oneTime, int dateOffset)
    {
        var date = Today.AddDays(dateOffset);
        var habit = CreateHabit(oneTime);
        habit.Log(date).IsSuccess.Should().BeTrue();
        var logId = habit.Logs.Single().Id;
        var options = await SeedAsync(habit);
        await using var context = new OrbitDbContext(options);
        var tool = CreateTool(context);
        var args = dateOffset != 0 ? Args(habit.Id, date) : Args(habit.Id);
        _streak.RecalculateAsync(UserId, false, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            (await context.HabitLogs.CountAsync()).Should().Be(0);
            return null;
        });

        var result = await tool.ExecuteAsync(args, UserId, default);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be(habit.Title);
        result.EntityId.Should().Be(habit.Id.ToString());
        await using var verify = new OrbitDbContext(options);
        var row = (await verify.HabitLogs.IgnoreQueryFilters().ToListAsync()).Should().ContainSingle().Subject;
        row.Id.Should().Be(logId);
        row.IsDeleted.Should().BeTrue();
        (await verify.Habits.SingleAsync()).IsCompleted.Should().BeFalse();
        await _streak.Received(1).RecalculateAsync(UserId, false, Arg.Any<CancellationToken>());
        await _goals.Received(1).SyncDerivedGoalsAsync(UserId, Arg.Any<IReadOnlyCollection<Guid>>(), Today,
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingLog_FailsWithoutWrites()
    {
        var habit = CreateHabit();
        habit.Log(Today.AddDays(-1)).IsSuccess.Should().BeTrue();
        await using var context = new OrbitDbContext(await SeedAsync(habit));

        var result = await CreateTool(context).ExecuteAsync(Args(habit.Id), UserId, default);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(DomainErrors.LogNotFoundForDate.Message);
        await _work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        context.ChangeTracker.HasChanges().Should().BeFalse();
        (await context.HabitLogs.IgnoreQueryFilters().ToListAsync()).Should().ContainSingle()
            .Which.IsDeleted.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 45)]
    [InlineData(true, 400)]
    public async Task CompletedGeneralTask_NoDate_UndoesLatestCompletionAndPreservesHistory(bool repeat, int daysEarlier)
    {
        var date = Today.AddDays(-daysEarlier);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.General, UserId, "Task", null, null, date,
            RepeatReminder: repeat ? new Orbit.Domain.ValueObjects.RepeatReminderOptions(480) : null)).Value;
        var older = habit.Log(date).Value;
        var latest = HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, date, 1, 1, older.CreatedAtUtc.AddTicks(1));
        habit.LoadScheduleLogsForRead([older, latest]);
        var options = await SeedAsync(habit);
        await using var context = new OrbitDbContext(options);
        var tool = CreateTool(context);

        var check = await tool.CheckArgumentsAsync(Args(habit.Id), UserId, default);

        check.IsSuccess.Should().BeTrue(check.Error);
        context.ChangeTracker.HasChanges().Should().BeFalse();
        await _work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        var result = await tool.ExecuteAsync(Args(habit.Id), UserId, default);

        result.Success.Should().BeTrue(result.Error);
        await using var verify = new OrbitDbContext(options);
        (await verify.Habits.SingleAsync()).IsCompleted.Should().BeFalse();
        var logs = await verify.HabitLogs.IgnoreQueryFilters().ToListAsync();
        logs.Should().HaveCount(2);
        logs.Single(log => log.Id == older.Id).IsDeleted.Should().BeFalse();
        logs.Single(log => log.Id == latest.Id).IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task ForeignHabit_FailsWithoutWritesOrExposingName()
    {
        var habit = CreateHabit(userId: Guid.NewGuid());
        habit.Log(Today).IsSuccess.Should().BeTrue();
        await using var context = new OrbitDbContext(await SeedAsync(habit));

        var result = await CreateTool(context).ExecuteAsync(Args(habit.Id), UserId, default);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(ErrorMessages.HabitNotOwned.Message);
        result.EntityName.Should().BeNull();
        await _work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        context.ChangeTracker.HasChanges().Should().BeFalse();
    }

    [Fact]
    public async Task CheckArguments_ValidCompletion_DoesNotMutateOrSave()
    {
        var habit = CreateHabit();
        habit.Log(Today).IsSuccess.Should().BeTrue();
        await using var context = new OrbitDbContext(await SeedAsync(habit));

        var result = await CreateTool(context).CheckArgumentsAsync(Args(habit.Id), UserId, default);

        result.IsSuccess.Should().BeTrue();
        context.ChangeTracker.HasChanges().Should().BeFalse();
        await _work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompletedRoot_QuotaDenied_DoesNotUnlog()
    {
        var habit = CreateHabit(oneTime: true);
        habit.Log(Today).IsSuccess.Should().BeTrue();
        await using var context = new OrbitDbContext(await SeedAsync(habit));
        _payGate.CanCreateHabits(UserId, 1, Arg.Any<CancellationToken>()).Returns(Result.Failure("quota"));

        var result = await CreateTool(context).ExecuteAsync(Args(habit.Id), UserId, default);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("quota");
        context.ChangeTracker.HasChanges().Should().BeFalse();
        await _work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"habit_id\":3}")]
    [InlineData("{\"habit_id\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"habit_id\":\"11111111-1111-1111-1111-111111111111\",\"date\":\"invalid\"}")]
    public async Task InvalidArguments_FailWithoutWrites(string json)
    {
        await using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var result = await CreateTool(context).ExecuteAsync(JsonSerializer.Deserialize<JsonElement>(json), UserId, default);
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        await _work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private UnlogHabitTool CreateTool(OrbitDbContext context)
    {
        _work.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(call => context.SaveChangesAsync(call.Arg<CancellationToken>()));
        return new UnlogHabitTool(new GenericRepository<Habit>(context), _dates, _work, _payGate, _streak, _goals);
    }

    private static Habit CreateHabit(bool oneTime = false, Guid? userId = null) =>
        Habit.Create(new HabitCreateParams(((FrequencyUnit?)(oneTime ? null : (FrequencyUnit?)FrequencyUnit.Day)) is null ? HabitSchedule.OneTime : HabitSchedule.Recurring, userId ?? UserId, "Beber água", oneTime ? null : FrequencyUnit.Day,
            oneTime ? null : 1, Today)).Value;

    private static JsonElement Args(Guid habitId, DateOnly? date = null) => date is { } value
        ? JsonSerializer.SerializeToElement(new { habit_id = habitId, date = value.ToString("yyyy-MM-dd") })
        : JsonSerializer.SerializeToElement(new { habit_id = habitId });

    private static async Task<DbContextOptions<OrbitDbContext>> SeedAsync(Habit habit)
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var seed = new OrbitDbContext(options);
        seed.Habits.Add(habit);
        await seed.SaveChangesAsync();
        return options;
    }
}
