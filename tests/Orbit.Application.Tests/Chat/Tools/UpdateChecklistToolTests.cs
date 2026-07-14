using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public class UpdateChecklistToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly UpdateChecklistTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();

    public UpdateChecklistToolTests() => _tool = new UpdateChecklistTool(_mediator);

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
        var habitId = Guid.NewGuid();

        var result = await Execute(
            $$"""{"habit_id": "{{habitId}}", "checklist_items": [{"text": "Warm up", "is_checked": true}, {"text": "Stretch"}]}""");

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(habitId.ToString());
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

        var result = await Execute($$"""{"habit_id": "{{Guid.NewGuid()}}", "checklist_items": []}""");

        result.Success.Should().BeTrue();
        captured!.ChecklistItems.Should().BeEmpty();
    }

    [Fact]
    public async Task CommandFails_PropagatesError()
    {
        _mediator.Send(Arg.Any<UpdateChecklistCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Habit not found."));

        var result = await Execute($$"""{"habit_id": "{{Guid.NewGuid()}}", "checklist_items": [{"text": "x"}]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Habit not found.");
    }

    private async Task<ToolResult> Execute(string json) =>
        await _tool.ExecuteAsync(JsonDocument.Parse(json).RootElement, UserId, CancellationToken.None);
}
