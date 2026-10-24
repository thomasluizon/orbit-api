using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Goals.Services;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    private static readonly int[] MagnesiumReminderTimes = [10];

    [Fact]
    public async Task ChangeSetRevision_PatchesOneMemberAndPreservesItsSibling()
    {
        var pending = await HoldTwoChangesAsync();
        var original = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var before = AccountChangeSet.Read(original.Arguments)!;
        var revision = await ParentRevisionService().ReviseAsync(_userId, pending.Id,
            new(pending.PreviewFingerprint!, pending.Items!.Select(item => new RevisedPendingOperationItem(item.ItemId,
                item.EntityName == "Magnesium" ? JsonSerializer.SerializeToElement(new { due_time = "21:30" }) : null)).ToArray()), default);
        revision.IsSuccess.Should().BeTrue(revision.Error);
        revision.PendingOperationId.Should().Be(pending.Id);
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var after = AccountChangeSet.Read(stored.Arguments)!;
        after.Single(member => member.OperationId == "update_habit").Arguments.GetRawText().Should().Be(before.Single(member => member.OperationId == "update_habit").Arguments.GetRawText());
        after.Single(member => member.OperationId == "create_sub_habit").Arguments.GetProperty("reminder_times")[0].GetInt32().Should().Be(10);
        after.Single(member => member.OperationId == "create_sub_habit").Arguments.GetProperty("due_time").GetString().Should().Be("21:30");
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;
        var applied = await _executor.ExecuteAsync(new(_userId, stored.OperationId, stored.Arguments,
            stored.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
        applied.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, applied.Operation.PolicyReason);
        await _habitRepo.Received(1).AddAsync(Arg.Is<Habit>(habit => habit.Title == "Magnesium"
            && habit.DueTime == new TimeOnly(21, 30) && habit.ReminderTimes.Single() == 10), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangeSetRevision_RejectingCombinedPreviewCancelsEveryMember()
    {
        var pending = await HoldTwoChangesAsync();
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var members = AccountChangeSet.Read(stored.Arguments)!;
        _pendingOperationStore.GetAwaiting(_userId).Should().ContainSingle().Which.PendingOperationId.Should().Be(pending.Id);
        var revision = await ParentRevisionService().ReviseAsync(_userId, pending.Id,
            new(pending.PreviewFingerprint!, []), default);
        revision.Cancelled.Should().BeTrue();
        foreach (var member in members)
        {
            _pendingOperationStore.GetExecution(_userId, member.PendingOperationId).Should().BeNull();
            _pendingOperationStore.Confirm(_userId, member.PendingOperationId).Should().BeNull();
        }
        _pendingOperationStore.GetAwaiting(_userId).Should().BeEmpty();
    }

    [Fact]
    public async Task ChangeSetRevision_RemovedMemberStaysReservedUntilBundleIsRejected()
    {
        var pending = await HoldTwoChangesAsync();
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var members = AccountChangeSet.Read(stored.Arguments)!;
        foreach (var member in members) _pendingOperationStore.Confirm(_userId, member.PendingOperationId).Should().BeNull();
        var revision = await ParentRevisionService().ReviseAsync(_userId, pending.Id,
            new(pending.PreviewFingerprint!, [new(pending.Items!.Single(item => item.EntityName != "Magnesium").ItemId)]), default);
        revision.IsSuccess.Should().BeTrue(revision.Error);
        _pendingOperationStore.GetAwaiting(_userId).Should().ContainSingle().Which.PendingOperationId.Should().Be(pending.Id);
        var rejected = await ParentRevisionService().ReviseAsync(_userId, pending.Id,
            new(revision.Preview!.PreviewFingerprint!, []), default);
        rejected.Cancelled.Should().BeTrue();
        foreach (var member in members) _pendingOperationStore.GetExecution(_userId, member.PendingOperationId).Should().BeNull();
    }

    private async Task<PendingAgentOperation> HoldTwoChangesAsync()
    {
        var parents = ParentAccount("en");
        _unitOfWork.PassThroughTransactions<Result<string>>();
        var update = new UpdateHabitCommandHandler(_habitRepo, Substitute.For<IGenericRepository<SentReminder>>(),
            _goalRepo, _payGate, Substitute.For<IGoalCompletionService>(), _userDateService, _unitOfWork, _flagCache, _tagRepo,
            Substitute.For<IAppConfigService>());
        _mediator.Send(Arg.Any<UpdateHabitFieldsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => update.Handle(call.Arg<UpdateHabitFieldsCommand>(), call.Arg<CancellationToken>()));
        ScriptToolCall("{}", "update_habit");
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = [
                    new("create_sub_habit", "magnesium", JsonSerializer.SerializeToElement(new { parent_habit_id = parents[0].Id, title = "Magnesium", frequency_unit = "Day", due_time = "21:00", reminder_enabled = true, reminder_times = MagnesiumReminderTimes })),
                    new("update_habit", "description", JsonSerializer.SerializeToElement(new { habit_id = parents[1].Id, description = "Quiet time" }))
                ],
                ConversationContext = new()
            }));
        var result = await CreateHandler().Handle(new(_userId, "Add Magnesium under Post Lunch and describe Before Bed as Quiet time"), default);
        result.IsSuccess.Should().BeTrue();
        return result.Value.PendingOperations.Should().ContainSingle().Which;
    }
}
