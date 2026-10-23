using System.Text.Json;
using System.Linq.Expressions;
using Orbit.Domain.Entities;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_RejectedWritesAcrossRounds_DoNotCountTowardRepeatGuard(bool sameHabit)
    {
        SetupUserAndPayGate();
        var habits = Enumerable.Range(1, BulkToolRepeatGuard.Threshold)
            .Select(index => CreateHabit($"Habit {index}")).ToArray();
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(habits);
        var tool = FakeTool("update_habit");
        tool.IsReadOnly.Returns(false);
        var handler = CreateHandler(tool);
        var dispatched = 0;
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new AgentExecuteOperationResponse(new AgentOperationResult(
                "update_habit", AgentCapabilityIds.HabitsWrite, AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                ++dispatched < BulkToolRepeatGuard.Threshold ? AgentOperationStatus.Denied : AgentOperationStatus.Succeeded,
                "Update", PolicyReason: dispatched < BulkToolRepeatGuard.Threshold ? "denied" : null)));
        AiResponse Round(int index) => ToolResponse("update_habit", $"retry_{index}",
            JsonSerializer.Serialize(new
            {
                habit_id = habits[sameHabit ? 0 : index].Id,
                description = "Shared description"
            }));
        SetupAiResponse(Round(0));
        var round = 0;
        var forwarded = new List<AiToolCallResult>();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                forwarded.AddRange(call.ArgAt<IReadOnlyList<AiToolCallResult>>(1));
                return Result.Success(++round < habits.Length ? Round(round) : new AiResponse { TextMessage = "Finished" });
            });

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Retry the changes"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        forwarded.Should().HaveCount(habits.Length);
        forwarded.Take(habits.Length - 1).Should().OnlyContain(item => !item.Success);
        forwarded[^1].Success.Should().BeTrue();
        dispatched.Should().Be(habits.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_RepeatedUpdatesAcrossRounds_BlocksAtThresholdAndAllowsDistinctChanges(bool distinctChanges)
    {
        SetupUserAndPayGate();
        var habits = Enumerable.Range(1, BulkToolRepeatGuard.Threshold)
            .Select(index => CreateHabit($"Habit {index}")).ToArray();
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(habits);
        var tool = FakeTool("update_habit");
        tool.IsReadOnly.Returns(false);
        tool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true));
        var handler = CreateHandler(tool);
        AiResponse Round(int index) => ToolResponse("update_habit", $"call_{index}",
            JsonSerializer.Serialize(new
            {
                habit_id = habits[index].Id,
                description = distinctChanges ? $"Description {index}" : "Shared description"
            }));
        SetupAiResponse(Round(0));
        var round = 0;
        var forwarded = new List<AiToolCallResult>();
        _aiIntentService.ContinueWithToolResultsAsync(
                Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                forwarded.AddRange(call.ArgAt<IReadOnlyList<AiToolCallResult>>(1));
                round++;
                return Result.Success(round < habits.Length ? Round(round) : new AiResponse { TextMessage = "Finished" });
            });

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Change these descriptions"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        forwarded.Should().HaveCount(BulkToolRepeatGuard.Threshold);
        forwarded.Take(BulkToolRepeatGuard.Threshold - 1).Should().OnlyContain(item => item.Success);
        forwarded[^1].Success.Should().Be(distinctChanges);
        if (!distinctChanges)
            forwarded[^1].Error.Should().Contain("bulk_update_habits");
        await _operationExecutor.Received(distinctChanges ? habits.Length : habits.Length - 1)
            .ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("bulk_update_habits", "Change the description of all my habits", "{\"filter\":{\"all\":true},\"updates\":{\"description\":\"Shared\"}}")]
    [InlineData("bulk_reschedule_habits", "Reschedule all to tomorrow", "{\"filter\":{\"all\":true},\"due_date\":\"2026-04-04\"}")]
    public async Task Handle_FullSetBulkIntent_UsesOneApprovalPreviewBeyondPaginationCap(
        string operationId, string message, string arguments)
    {
        SetupUserAndPayGate();
        var habits = Enumerable.Range(1, 251).Select(index => CreateHabit($"Habit {index}")).ToArray();
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        IAiTool tool = operationId == "bulk_update_habits"
            ? new BulkUpdateHabitsTool(_mediator) : new BulkRescheduleHabitsTool(_mediator);
        var handler = CreateHandler(tool);
        var held = HoldReplayWrites(tool);
        SetupAiResponse(ToolResponse(operationId, "bulk", arguments));
        ReplyToReplay("Review the proposed changes.");

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, message), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        held.Should().ContainSingle().Which.OperationId.Should().Be(operationId);
        var preview = result.Value.PendingOperations.Should().ContainSingle().Subject;
        preview.ChangeTargetCount.Should().Be(251);
        preview.Items.Should().HaveCount(251);
        preview.Items!.Select(item => item.EntityId).Should().BeEquivalentTo(habits.Select(habit => habit.Id));
        habits.Should().OnlyContain(habit => habit.Description == null && habit.NextDueDate == Today);
        await _mediator.DidNotReceive().Send(Arg.Any<BulkUpdateHabitsCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TruncatedContinuationWithTools_StopsBeforeDispatchAndDoesNotContinue()
    {
        SetupUserAndPayGate();
        var tool = FakeTool("update_habit");
        tool.IsReadOnly.Returns(false);
        tool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true));
        var handler = CreateHandler(tool);
        SetupAiResponse(ToolResponse("update_habit", "first", "{}"));
        _aiIntentService.ContinueWithToolResultsAsync(
                Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ToolResponse("update_habit", "cut_off", "{}") with
            {
                TextMessage = "First part",
                IsTruncated = true
            }));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Change descriptions"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Contain("cut off before completion");
        await _operationExecutor.Received(1).ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>());
        await _aiIntentService.ReceivedWithAnyArgs(1).ContinueWithToolResultsAsync(default!, default!, default, default);
    }
}
