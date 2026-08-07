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

public class UpdateChecklistToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly UpdateChecklistTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();

    public UpdateChecklistToolTests() => _tool = new UpdateChecklistTool(_mediator, _habitRepo);

    [Fact]
    public void Metadata_IsExposed()
    {
        _tool.Name.Should().Be("update_checklist");
        _tool.GetParameterSchema().Should().NotBeNull();
    }

    [Fact]
    public async Task MissingHabitId_ReturnsError()
    {
        var result = await Execute("""{"checklist_items": []}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_id is required");
    }

    [Fact]
    public async Task InvalidHabitId_ReturnsError()
    {
        var result = await Execute("""{"habit_id": "x", "checklist_items": []}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_id is required");
    }

    [Fact]
    public async Task MissingChecklistItems_ReturnsError()
    {
        var result = await Execute($$"""{"habit_id": "{{Guid.NewGuid()}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("checklist_items is required");
    }

    [Fact]
    public async Task ChecklistItemsNotArray_ReturnsError()
    {
        var result = await Execute($$"""{"habit_id": "{{Guid.NewGuid()}}", "checklist_items": 5}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("checklist_items is required");
    }

    [Fact]
    public async Task ValidItems_ForwardsCommand_ReturnsSuccess()
    {
        UpdateChecklistCommand? captured = null;
        _mediator.Send(Arg.Any<UpdateChecklistCommand>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => { captured = callInfo.Arg<UpdateChecklistCommand>(); return Result.Success(); });
        var habit = CreateHabit("Morning mobility");
        SetupHabitFound(habit);

        var result = await Execute(
            $$"""{"habit_id": "{{habit.Id}}", "checklist_items": [{"text": "Warm up", "is_checked": true}, {"text": "Stretch"}]}""");

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(habit.Id.ToString());
        result.EntityName.Should().Be("Morning mobility");
        captured!.ChecklistItems.Should().HaveCount(2);
        captured.ChecklistItems[0].Text.Should().Be("Warm up");
        captured.ChecklistItems[0].IsChecked.Should().BeTrue();
    }

    [Fact]
    public async Task EmptyItems_ClearsChecklist_ReturnsSuccess()
    {
        UpdateChecklistCommand? captured = null;
        _mediator.Send(Arg.Any<UpdateChecklistCommand>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => { captured = callInfo.Arg<UpdateChecklistCommand>(); return Result.Success(); });
        var habit = CreateHabit("Morning mobility");
        SetupHabitFound(habit);

        var result = await Execute($$"""{"habit_id": "{{habit.Id}}", "checklist_items": []}""");

        result.Success.Should().BeTrue();
        captured!.ChecklistItems.Should().BeEmpty();
    }

    [Fact]
    public async Task CommandFails_PropagatesError()
    {
        var habit = CreateHabit("Morning mobility");
        SetupHabitFound(habit);
        _mediator.Send(Arg.Any<UpdateChecklistCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Habit not found."));

        var result = await Execute($$"""{"habit_id": "{{habit.Id}}", "checklist_items": [{"text": "x"}]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Habit not found.");
        result.EntityName.Should().BeNull();
    }

    [Fact]
    public async Task UnresolvableHabit_ReturnsNullEntityName()
    {
        var result = await Execute($$"""{"habit_id": "{{Guid.NewGuid()}}", "checklist_items": []}""");

        result.Success.Should().BeFalse();
        result.EntityName.Should().BeNull();
        await _mediator.DidNotReceive().Send(Arg.Any<UpdateChecklistCommand>(), Arg.Any<CancellationToken>());
    }

    private void SetupHabitFound(Habit habit) =>
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
        .Returns(habit);

    private static Habit CreateHabit(string title) =>
        Habit.Create(new HabitCreateParams(UserId, title, FrequencyUnit.Day, 1, new DateOnly(2026, 8, 6))).Value;

    private async Task<ToolResult> Execute(string json) =>
        await _tool.ExecuteAsync(JsonDocument.Parse(json).RootElement, UserId, CancellationToken.None);
}

internal static class HabitToolTestFactory
{
    public static MoveHabitParentTool CreateMoveHabitParentTool(
        IMediator mediator,
        Guid userId,
        string title)
    {
        var habitRepository = Substitute.For<IGenericRepository<Habit>>();
        var habit = Habit.Create(new HabitCreateParams(
            userId,
            title,
            FrequencyUnit.Day,
            1,
            new DateOnly(2026, 8, 6))).Value;

        habitRepository.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
        .Returns(habit);

        return new MoveHabitParentTool(mediator, habitRepository);
    }
}
