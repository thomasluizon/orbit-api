using System.Text.Json;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Orbit.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public class LogHabitToolTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly LogHabitTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public LogHabitToolTests()
    {
        _tool = new LogHabitTool(_mediator, _habitRepo, _userDateService);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _mediator.Send(Arg.Any<LogHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LogHabitResponse(Guid.NewGuid(), false, 0)));
    }

    [Fact]
    public async Task SuccessfulLog_ReturnsSuccessAndCreatesLog()
    {
        var habit = CreateHabit("Water", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Water");
        await _mediator.Received(1).Send(
            Arg.Is<LogHabitCommand>(command => command.HabitId == habit.Id && command.Date == Today),
            Arg.Any<CancellationToken>());
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
    public async Task WrongUser_CannotLogAnothersHabit()
    {
        var habit = CreateHabit("Owner-only habit", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);
        var attackerId = Guid.NewGuid();
        var attackerResult = await _tool.ExecuteAsync(ArgsFor(habit.Id), attackerId, CancellationToken.None);

        attackerResult.Success.Should().BeFalse();
        attackerResult.Error.Should().Be(ErrorMessages.HabitNotOwned.Message);
        await _mediator.DidNotReceive().Send(Arg.Any<LogHabitCommand>(), Arg.Any<CancellationToken>());
    }

    private static JsonElement ArgsFor(Guid habitId) =>
        JsonDocument.Parse($$"""{"habit_id":"{{habitId}}"}""").RootElement;

    [Fact]
    public async Task IgnoresUnknownNoteArgument_AndLogsSuccessfully()
    {
        var habit = CreateHabit("Exercise", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "note": "30 min run"}""");

        result.Success.Should().BeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<LogHabitCommand>(command => command.Date == Today),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AlreadyCompleted_ReturnsError()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId, "Task", null, null, DueDate: Today)).Value;
        habit.Log(Today);
        SetupHabitFound(habit);
        _mediator.Send(Arg.Any<LogHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<LogHabitResponse>(DomainErrors.CannotLogCompletedHabit));

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task FlexibleHabit_RejectsLogAfterWindowTarget()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Flexible,
            UserId, "Flexible", FrequencyUnit.Week, 2, DueDate: Today)).Value;
        habit.Log(Today).IsSuccess.Should().BeTrue();
        SetupHabitFound(habit);
        _mediator.Send(Arg.Any<LogHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var command = call.ArgAt<LogHabitCommand>(0);
                var log = habit.Log(command.Date!.Value);
                var response = log.IsSuccess
                    ? Result.Success(new LogHabitResponse(log.Value.Id, false, 0))
                    : Result.Failure<LogHabitResponse>(log.Error, log.ErrorCode!);
                return Task.FromResult(response);
            });

        var accepted = await Execute($$$"""{"habit_id": "{{{habit.Id}}}"}""");
        var rejected = await Execute($$$"""{"habit_id": "{{{habit.Id}}}"}""");

        accepted.Success.Should().BeTrue();
        rejected.Success.Should().BeFalse();
        rejected.Error.Should().Be("All instances for this period have already been completed or skipped.");
        habit.Logs.Count(log => log.Value > 0).Should().Be(2);
    }

    [Fact]
    public async Task FutureDate_ReturnsError()
    {
        var habit = CreateHabit("Water", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "date": "2026-04-10"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Be(ErrorMessages.CannotLogFutureDate.Message);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistedCompletion_FromDisposedContext_FailsWithoutDispatchOrNewRows(bool explicitPastDate)
    {
        var date = explicitPastDate ? Today.AddDays(-2) : Today;
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var habit = CreateHabit("Water", FrequencyUnit.Day, 1);
        habit.Log(date).IsSuccess.Should().BeTrue();
        await using (var seed = new OrbitDbContext(options))
        {
            seed.Habits.Add(habit);
            await seed.SaveChangesAsync();
        }
        await using var context = new OrbitDbContext(options);
        var tool = new LogHabitTool(_mediator, new GenericRepository<Habit>(context), _userDateService);
        var args = explicitPastDate
            ? JsonSerializer.SerializeToElement(new { habit_id = habit.Id, date = date.ToString("yyyy-MM-dd") })
            : ArgsFor(habit.Id);

        var result = await tool.ExecuteAsync(args, UserId, default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("already");
        await _mediator.DidNotReceive().Send(Arg.Any<LogHabitCommand>(), Arg.Any<CancellationToken>());
        (await context.SaveChangesAsync()).Should().Be(0);
        (await context.HabitLogs.IgnoreQueryFilters().ToListAsync()).Should().ContainSingle()
            .Which.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task SuccessfulLog_DisablesToggleForTheRequestedDate()
    {
        var habit = CreateHabit("Water", FrequencyUnit.Day, 1);
        SetupHabitFound(habit);
        var date = Today.AddDays(-1);

        var result = await _tool.ExecuteAsync(JsonSerializer.SerializeToElement(new
        {
            habit_id = habit.Id,
            date = date.ToString("yyyy-MM-dd")
        }), UserId, default);

        result.Success.Should().BeTrue();
        await _mediator.Received(1).Send(Arg.Is<LogHabitCommand>(command =>
            command.HabitId == habit.Id && command.Date == date && !command.ToggleExisting),
            Arg.Any<CancellationToken>());
    }

    private static Habit CreateHabit(string title, FrequencyUnit? freq, int? qty)
    {
        return Habit.Create(new HabitCreateParams(((FrequencyUnit?)(freq)) is null ? HabitSchedule.OneTime : HabitSchedule.Recurring, UserId, title, freq, qty, DueDate: Today)).Value;
    }

    private void SetupHabitFound(Habit habit)
    {
        _habitRepo.GetByIdAsync(habit.Id, Arg.Any<CancellationToken>()).Returns(habit);
        _habitRepo.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Expression<Func<Habit, bool>>>().Compile()(habit) ? habit : null);
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
