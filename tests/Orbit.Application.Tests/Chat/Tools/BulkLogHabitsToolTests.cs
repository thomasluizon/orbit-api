using System.Linq.Expressions;
using System.Text.Json;
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

namespace Orbit.Application.Tests.Chat.Tools;

public class BulkLogHabitsToolTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly BulkLogHabitsTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public BulkLogHabitsToolTests()
    {
        _tool = new BulkLogHabitsTool(_mediator, _habitRepo, _userDateService);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _mediator.Send(Arg.Any<BulkLogHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var command = call.Arg<BulkLogHabitsCommand>();
                var items = command.Items.Select((item, index) => new BulkLogItemResult(
                    index, BulkItemStatus.Success, item.HabitId, Guid.NewGuid())).ToList();
                return Result.Success(new BulkLogResult(items));
            });
    }

    [Fact]
    public async Task LogMultiple_ReturnsLoggedNames()
    {
        var h1 = CreateHabit("Water");
        var h2 = CreateHabit("Exercise");
        SetupHabitsFound(h1, h2);

        var result = await Execute($$$"""{"habit_ids": ["{{{h1.Id}}}", "{{{h2.Id}}}"]}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("Water");
        result.EntityName.Should().Contain("Exercise");
        await _mediator.Received(1).Send(
            Arg.Is<BulkLogHabitsCommand>(command => command.Items.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SomeNotFound_LogsFoundOnes()
    {
        var h1 = CreateHabit("Water");
        var missingId = Guid.NewGuid();
        SetupHabitsFound(h1);
        var result = await Execute($$$"""{"habit_ids": ["{{{h1.Id}}}", "{{{missingId}}}"]}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("Water");
    }

    [Fact]
    public async Task AllNotFound_ReturnsError()
    {
        SetupHabitsFound();
        var id1 = Guid.NewGuid();
        var result = await Execute($$$"""{"habit_ids": ["{{{id1}}}"]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No habits were logged");
    }

    [Fact]
    public async Task AlreadyLogged_SkipsAlreadyLogged()
    {
        var logged = CreateHabit("Water");
        var fresh = CreateHabit("Exercise");
        SetupHabitsFound(logged, fresh);
        _mediator.Send(Arg.Any<BulkLogHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new BulkLogResult([
                new(0, BulkItemStatus.Success, logged.Id),
                new(1, BulkItemStatus.Success, fresh.Id, Guid.NewGuid())])));

        var result = await Execute($$$"""{"habit_ids": ["{{{logged.Id}}}", "{{{fresh.Id}}}"]}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("Exercise");
        result.EntityName.Should().NotContain("Water");
    }

    [Fact]
    public async Task EmptyIdList_ReturnsError()
    {
        var result = await Execute("""{"habit_ids": []}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No valid habit IDs");
    }

    [Fact]
    public async Task MissingHabitIds_ReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_ids is required");
    }

    [Fact]
    public async Task IgnoresUnknownNoteArgument_AndLogsSuccessfully()
    {
        var h1 = CreateHabit("Water");
        SetupHabitsFound(h1);

        var result = await Execute($$$"""{"habit_ids": ["{{{h1.Id}}}"], "note": "Morning routine"}""");

        result.Success.Should().BeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<BulkLogHabitsCommand>(command => command.Items.Single().Date == Today),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WrongUser_CannotLogAnothersHabit()
    {
        var habit = CreateHabit("Owner-only habit");
        SetupHabitsFound(habit);
        var attackerId = Guid.NewGuid();
        var attackerResult = await _tool.ExecuteAsync(ArgsFor(habit.Id), attackerId, CancellationToken.None);

        attackerResult.Success.Should().BeFalse();
        attackerResult.Error.Should().Contain("No habits were logged");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkLogHabitsCommand>(), Arg.Any<CancellationToken>());
    }

    private static JsonElement ArgsFor(Guid habitId) =>
        JsonDocument.Parse($$"""{"habit_ids":["{{habitId}}"]}""").RootElement;

    private static Habit CreateHabit(string title)
    {
        return Habit.Create(new HabitCreateParams(UserId, title, FrequencyUnit.Day, 1, DueDate: Today)).Value;
    }

    private void SetupHabitsFound(params Habit[] habits)
    {
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>()
        ).Returns(callInfo =>
        {
            var predicate = callInfo.ArgAt<Expression<Func<Habit, bool>>>(0).Compile();
            return habits.Where(predicate).ToList();
        });
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
