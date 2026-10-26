using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData("en", "make it a checklist item instead", HabitPlacement.ChecklistItem)]
    [InlineData("en", "make it a sub-habit instead", HabitPlacement.SubHabit)]
    [InlineData("en", "make it a separate habit instead", HabitPlacement.NewParent)]
    [InlineData("pt-BR", "faça dele um item de lista", HabitPlacement.ChecklistItem)]
    [InlineData("pt-BR", "mude para um sub-hábito", HabitPlacement.SubHabit)]
    [InlineData("pt-BR", "mude para um hábito separado", HabitPlacement.NewParent)]
    public async Task PlacementSentence_UsesStoredRevisionWithoutCallingCompletion(string language, string message, string selected)
    {
        var parents = ParentAccount(language);
        parents[0].UpdateChecklist([new("Coffee", true)]);
        var arguments = JsonSerializer.Serialize(new
        {
            parent_habit_id = parents[0].Id,
            title = "Stretch",
            frequency_unit = "Day",
            due_time = "09:00",
            reminder_enabled = true,
            reminder_times = new[] { 15 },
            description = "Gently"
        });
        ScriptToolCall(arguments, "create_sub_habit");
        var handler = CreateHandler();
        var initial = await handler.Handle(new(_userId, "Add Stretch to Post Lunch"), default);
        initial.IsSuccess.Should().BeTrue(initial.Error);
        var pending = initial.Value.PendingOperations.Should().ContainSingle().Which;
        var before = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        _aiIntentService.ClearReceivedCalls();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result<AiResponse>>(new InvalidOperationException("Placement must not call the completion client.")));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result<AiResponse>>(new InvalidOperationException("Placement must not call the completion client.")));

        var reply = await handler.Handle(new ProcessUserChatCommand(_userId, message,
            ClientContext: new AgentClientContext(SupportsParts: true)), default);

        reply.IsSuccess.Should().BeTrue(reply.Error);
        var revised = reply.Value.PendingOperations.Should().ContainSingle().Which;
        revised.Id.Should().Be(pending.Id);
        revised.Items!.Single().Placement!.Selected.Should().Be(selected);
        var after = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var previousItem = before.Arguments.TryGetProperty("habits", out var previousHabits) ? previousHabits[0] : before.Arguments;
        var currentItem = after.Arguments.TryGetProperty("habits", out var currentHabits) ? currentHabits[0] : after.Arguments;
        foreach (var property in previousItem.EnumerateObject().Where(property => property.Name != "placement"))
            JsonElement.DeepEquals(currentItem.GetProperty(property.Name), property.Value).Should().BeTrue(property.Name);
        await _aiIntentService.DidNotReceiveWithAnyArgs().SendWithToolsAsync(default!, default, default);
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        parents[0].ChecklistItems.Should().Equal(new ChecklistItem("Coffee", true));
    }

    [Fact]
    public async Task Placement_LegacyParentEditCanClearDestination()
    {
        var parents = ParentAccount("en");
        ScriptToolCall(JsonSerializer.Serialize(new
        {
            habits = new[] {
            new { title = "Stretch", parent_habit_id = parents[0].Id, frequency_unit = "Day" }
        }
        }), "bulk_create_habits");
        var held = await CreateHandler().Handle(new(_userId, "Add Stretch to Post Lunch"), default);
        var pending = held.Value.PendingOperations.Should().ContainSingle().Which;

        var revision = await ParentRevisionService().ReviseAsync(_userId, pending.Id,
            new(pending.PreviewFingerprint!, [new(pending.Items!.Single().ItemId,
                JsonSerializer.SerializeToElement(new { parent_habit_id = (Guid?)null }))]), default);

        revision.IsSuccess.Should().BeTrue(revision.Error);
        revision.Preview!.Items!.Single().Placement.Should().BeNull();
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        stored.Arguments.GetProperty("habits")[0].TryGetProperty("placement", out _).Should().BeFalse();
        stored.Arguments.GetProperty("habits")[0].TryGetProperty("parent_habit_id", out _).Should().BeFalse();
    }

    [Fact]
    public async Task PlacementSwitch_CompoundPreviewPreservesSiblingAndLegacyApproval()
    {
        var pending = await HoldTwoChangesAsync();
        var before = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var originalMembers = AccountChangeSet.Read(before.Arguments)!;
        var revision = await ParentRevisionService().ReviseAsync(_userId, pending.Id,
            new(pending.PreviewFingerprint!, pending.Items!.Select(item => new RevisedPendingOperationItem(item.ItemId,
                Placement: item.Placement is not null ? HabitPlacement.SubHabit : null)).ToArray()), default);
        revision.IsSuccess.Should().BeTrue(revision.Error);
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var members = AccountChangeSet.Read(stored.Arguments)!;
        JsonElement.DeepEquals(members.Single(member => member.OperationId == "update_habit").Arguments,
            originalMembers.Single(member => member.OperationId == "update_habit").Arguments).Should().BeTrue();
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;
        var applied = await _executor.ExecuteAsync(new(_userId, stored.OperationId, stored.Arguments,
            stored.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
        applied.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, applied.Operation.PolicyReason);
    }
}
