using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Application.Tests.Chat.Tools;

public class SkipHabitToolTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly SkipHabitTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public SkipHabitToolTests()
    {
        _tool = new SkipHabitTool(_habitRepo, _habitLogRepo, _userDateService);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    [Fact]
    public async Task SuccessfulSkip_RecurringHabit_AdvancesDueDate()
    {
        var habit = CreateHabit("Water", FrequencyUnit.Day, 1, Today);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Water");
        habit.DueDate.Should().BeAfter(Today);
    }

    [Fact]
    public async Task OneTimeTask_PostponesToTomorrow()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Buy milk", null, null, DueDate: Today)).Value;
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}"}""");

        result.Success.Should().BeTrue();
        habit.DueDate.Should().Be(Today.AddDays(1));
    }

    [Fact]
    public async Task HabitNotFound_ReturnsError()
    {
        var id = Guid.NewGuid();
        SetupHabitNotFound();

        var result = await Execute($$$"""{"habit_id": "{{{id}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task CompletedHabit_ReturnsError()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Task", null, null, DueDate: Today)).Value;
        habit.Log(Today);        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("completed");
    }

    [Fact]
    public async Task NotScheduledToday_NonFlexible_ReturnsError()
    {
        var habit = CreateHabit("Future", FrequencyUnit.Day, 1, Today.AddDays(5));
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not yet due");
    }

    [Fact]
    public async Task FutureDate_ReturnsError()
    {
        var habit = CreateHabit("Water", FrequencyUnit.Day, 1, Today);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "date": "2026-04-10"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("future");
    }

    [Fact]
    public async Task InvalidDateFormat_ReturnsError()
    {
        var habit = CreateHabit("Water", FrequencyUnit.Day, 1, Today);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "date": "invalid"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Invalid date format");
    }

    [Fact]
    public async Task MissingHabitId_ReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_id is required");
    }

    [Fact]
    public async Task FlexibleHabit_SkipsAndCreatesLog()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Exercise", FrequencyUnit.Week, 3,
            DueDate: Today, IsFlexible: true)).Value;
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}"}""");

        result.Success.Should().BeTrue();
        await _habitLogRepo.Received(1).AddAsync(Arg.Any<HabitLog>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WrongUser_CannotSkipAnothersHabit_OwnerCan_RealContext()
    {
        var databaseName = $"SkipHabitIsolation_{Guid.NewGuid()}";
        Guid habitId;
        await using (var seed = CreateContext(databaseName))
        {
            var ownerHabit = CreateHabit("Owner-only habit", FrequencyUnit.Day, 1, Today);
            seed.Habits.Add(ownerHabit);
            await seed.SaveChangesAsync();
            habitId = ownerHabit.Id;
        }

        await using var context = CreateContext(databaseName);
        var userDateService = Substitute.For<IUserDateService>();
        userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Today);
        var tool = new SkipHabitTool(
            new GenericRepository<Habit>(context),
            new GenericRepository<HabitLog>(context),
            userDateService);

        var attackerId = Guid.NewGuid();
        var attackerResult = await tool.ExecuteAsync(ArgsFor(habitId), attackerId, CancellationToken.None);
        await context.SaveChangesAsync();

        attackerResult.Success.Should().BeFalse();
        attackerResult.Error.Should().Contain("not found");
        await using (var afterAttack = CreateContext(databaseName))
            (await afterAttack.Habits.SingleAsync(h => h.Id == habitId)).DueDate
                .Should().Be(Today, "a foreign user must not advance another user's schedule");

        var ownerResult = await tool.ExecuteAsync(ArgsFor(habitId), UserId, CancellationToken.None);
        await context.SaveChangesAsync();

        ownerResult.Success.Should().BeTrue("the owner can skip their own habit");
        await using (var afterOwner = CreateContext(databaseName))
            (await afterOwner.Habits.SingleAsync(h => h.Id == habitId)).DueDate
                .Should().BeAfter(Today);
    }

    private static JsonElement ArgsFor(Guid habitId) =>
        JsonDocument.Parse($$"""{"habit_id":"{{habitId}}"}""").RootElement;

    private static OrbitDbContext CreateContext(string databaseName) =>
        new(new DbContextOptionsBuilder<OrbitDbContext>().UseInMemoryDatabase(databaseName).Options);

    private static Habit CreateHabit(string title, FrequencyUnit? freq, int? qty, DateOnly dueDate)
    {
        return Habit.Create(new HabitCreateParams(UserId, title, freq, qty, DueDate: dueDate)).Value;
    }

    private void SetupHabitFound(Habit habit)
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns(habit);
    }

    private void SetupHabitNotFound()
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns((Habit?)null);
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
