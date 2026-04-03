using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class LogHabitToolTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly LogHabitTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public LogHabitToolTests()
    {
        _tool = new LogHabitTool(_habitRepo, _habitLogRepo, _userDateService);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    [Fact]
    public async Task SuccessfulLog_ReturnsSuccessAndCreatesLog()
    {
        var habit = CreateHabit("Water", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Water");
        await _habitLogRepo.Received(1).AddAsync(Arg.Any<HabitLog>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HabitNotFound_ReturnsError()
    {
        var id = Guid.NewGuid();
        _habitRepo.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((Habit?)null);

        var result = await Execute($$$"""{"habit_id": "{{{id}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task HabitNotOwned_ReturnsError()
    {
        var otherUserId = Guid.NewGuid();
        var habit = Habit.Create(new HabitCreateParams(otherUserId, "Other", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        _habitRepo.GetByIdAsync(habit.Id, Arg.Any<CancellationToken>()).Returns(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("does not belong");
    }

    [Fact]
    public async Task WithNote_PassesNoteToLog()
    {
        var habit = CreateHabit("Exercise", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "note": "30 min run"}""");

        result.Success.Should().BeTrue();
        await _habitLogRepo.Received(1).AddAsync(Arg.Any<HabitLog>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AlreadyCompleted_ReturnsError()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Task", null, null, DueDate: Today)).Value;
        habit.Log(Today); // Complete one-time task
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task FutureDate_ReturnsError()
    {
        var habit = CreateHabit("Water", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "date": "2026-04-10"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("future");
    }

    [Fact]
    public async Task InvalidDateFormat_ReturnsError()
    {
        var habit = CreateHabit("Water", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "date": "not-a-date"}""");

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
    public async Task PastDate_LogsSuccessfully()
    {
        var habit = CreateHabit("Read", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "date": "2026-04-01"}""");

        result.Success.Should().BeTrue();
    }

    private static Habit CreateHabit(string title, FrequencyUnit? freq, int? qty)
    {
        return Habit.Create(new HabitCreateParams(UserId, title, freq, qty, DueDate: Today)).Value;
    }

    private void SetupHabitFound(Habit habit)
    {
        _habitRepo.GetByIdAsync(habit.Id, Arg.Any<CancellationToken>()).Returns(habit);
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
