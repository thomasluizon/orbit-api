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
using Orbit.Application.Common;

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
        _tool.Description.Should().Contain("send only item_to_add");
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
        var habit = HabitToolTestFactory.CreateHabit(UserId, "Morning mobility");
        HabitToolTestFactory.SetupHabitFound(_habitRepo, habit);

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
        var habit = HabitToolTestFactory.CreateHabit(UserId, "Morning mobility");
        HabitToolTestFactory.SetupHabitFound(_habitRepo, habit);

        var result = await Execute($$"""{"habit_id": "{{habit.Id}}", "checklist_items": []}""");

        result.Success.Should().BeTrue();
        captured!.ChecklistItems.Should().BeEmpty();
    }

    [Fact]
    public async Task CommandFails_PropagatesError()
    {
        var habit = HabitToolTestFactory.CreateHabit(UserId, "Morning mobility");
        HabitToolTestFactory.SetupHabitFound(_habitRepo, habit);
        _mediator.Send(Arg.Any<UpdateChecklistCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorMessages.HabitNotFound));

        var result = await Execute($$"""{"habit_id": "{{habit.Id}}", "checklist_items": [{"text": "x"}]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Be(ErrorMessages.HabitNotFound.Message);
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

    [Fact]
    public async Task ForeignHabit_LookupRejectsCallerAndDoesNotExposeName()
    {
        var foreign = HabitToolTestFactory.CreateHabit(Guid.NewGuid(), "Private title");
        HabitToolTestFactory.SetupHabitFound(_habitRepo, foreign);

        var result = await Execute($$"""{"habit_id": "{{foreign.Id}}", "checklist_items": []}""");

        result.EntityName.Should().BeNull();
        result.Success.Should().BeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<UpdateChecklistCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("overLimit")]
    public async Task Append_InvalidTextCannotDispatch(string text)
    {
        var habit = HabitToolTestFactory.CreateHabit(UserId, "Water");
        HabitToolTestFactory.SetupHabitFound(_habitRepo, habit);
        if (text == "overLimit") text = new string('a', AppConstants.MaxChecklistItemTextLength + 1);

        var result = await Execute(JsonSerializer.Serialize(new { habit_id = habit.Id, item_to_add = text }));

        result.Success.Should().BeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<AppendChecklistItemCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Append_RejectsFullChecklistAndForeignTarget()
    {
        var habit = HabitToolTestFactory.CreateHabit(UserId, "Water");
        habit.UpdateChecklist(Enumerable.Range(0, AppConstants.MaxChecklistItems)
            .Select(index => new Orbit.Domain.ValueObjects.ChecklistItem($"Item {index}", index == 0)).ToArray());
        HabitToolTestFactory.SetupHabitFound(_habitRepo, habit);
        (await Execute(JsonSerializer.Serialize(new { habit_id = habit.Id, item_to_add = "Get a glass" }))).Success.Should().BeFalse();

        var foreign = HabitToolTestFactory.CreateHabit(Guid.NewGuid(), "Private title");
        HabitToolTestFactory.SetupHabitFound(_habitRepo, foreign);
        var result = await Execute(JsonSerializer.Serialize(new { habit_id = foreign.Id, item_to_add = "Get a glass" }));

        result.Success.Should().BeFalse();
        result.EntityName.Should().BeNull();
        await _mediator.DidNotReceive().Send(Arg.Any<AppendChecklistItemCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("\"Get a glass\",\"checklist_items\":[]")]
    [InlineData("null")]
    [InlineData("42")]
    public async Task Append_RejectsConflictingOrMalformedArguments(string value)
    {
        var result = await Execute($$"""{"habit_id":"{{Guid.NewGuid()}}","item_to_add":{{value}}}""");

        result.Success.Should().BeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<AppendChecklistItemCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("text")]
    [InlineData("order")]
    [InlineData("checked")]
    public async Task FullListAddition_RejectsChangesToStoredPrefix(string change)
    {
        var habit = HabitToolTestFactory.CreateHabit(UserId, "Water");
        habit.UpdateChecklist([new("Get a glass", true), new("Fill the glass", false)]);
        HabitToolTestFactory.SetupHabitFound(_habitRepo, habit);
        var items = habit.ChecklistItems.ToArray();
        if (change == "order") Array.Reverse(items);
        else items[0] = new(change == "text" ? "Changed text" : items[0].Text, false);
        var args = JsonSerializer.SerializeToElement(new
        {
            habit_id = habit.Id,
            checklist_items = items.Append(new("Touch the glass", false)).Select(item => new { text = item.Text, is_checked = item.IsChecked })
        });

        (await _tool.CheckArgumentsAsync(args, UserId, default)).IsFailure.Should().BeTrue();
        (await _tool.ExecuteAsync(args, UserId, default)).Success.Should().BeFalse();
        habit.ChecklistItems.Should().Equal(new Orbit.Domain.ValueObjects.ChecklistItem("Get a glass", true), new("Fill the glass", false));
        await _mediator.DidNotReceive().Send(Arg.Any<UpdateChecklistCommand>(), Arg.Any<CancellationToken>());
    }

    private async Task<ToolResult> Execute(string json) =>
        await _tool.ExecuteAsync(JsonDocument.Parse(json).RootElement, UserId, CancellationToken.None);
}
