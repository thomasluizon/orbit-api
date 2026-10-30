using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AskRerun_PrematureCapturedQuestionRecoversRenameAndSkip(bool composition)
    {
        SetupUserAndPayGate();
        if (composition) EnableComposition();
        var water = CreateHabit("Water");
        var walk = CreateHabit("Walk");
        SetupSelectionHabits(water, walk);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var update = new UpdateHabitTool(_habitRepo, _userDateService, _mediator);
        var skip = new SkipHabitTool(_mediator, _habitRepo);
        var handler = CreateHandler(RealAskTool(), query, update, skip);
        var held = HoldAskRerunWrites(query, update, skip);
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = AskRerunCalls("compound-update-skip-en-r1")
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ConversationContext = TestConversationContext,
                ToolCalls = AskRerunWrites(water, walk)
            }));

        var result = await handler.Handle(new(UserId,
            $"Rename Water ({water.Id}) to Drink water, and skip Walk ({walk.Id}) for today.",
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true,
                PartKinds: ["choice", "habitList"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var preview = result.Value.PendingOperations.Should().ContainSingle().Subject;
        preview.Items.Should().HaveCount(2);
        preview.Items.Should().Contain(item => item.EntityId == water.Id
            && item.Fields.Any(field => field.Field == "title" && field.NewValue == "Drink water"));
        preview.Items.Should().Contain(item => item.EntityId == walk.Id
            && item.Fields.Any(field => field.Field == "action" && field.NewValue == "Skip"));
        held.Select(call => call.OperationId).Should().Equal("update_habit", "skip_habit");
        result.Value.Parts?.OfType<ChatBlockPart>().Should().NotContain(part => part.Kind == "choice");
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(TestConversationContext,
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Any(call => call.Name == "ask" && !call.Success)
                && results.Any(call => call.Name == "query_habits" && call.Success)),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceiveWithAnyArgs().Send(default(Orbit.Application.Habits.Commands.UpdateHabitCommand)!, default);
        water.Title.Should().Be("Water");
        walk.IsCompleted.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task AskRerun_RealAmbiguityKeepsIndependentPreviewAndBlocksCandidate(bool composition, bool askFirst, bool nested)
    {
        SetupUserAndPayGate();
        if (composition) EnableComposition();
        var water = CreateHabit("Water");
        var walks = new[] { CreateHabit("Walk"), CreateHabit("Walk") };
        SetupSelectionHabits(water, walks[0], walks[1]);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var update = new UpdateHabitTool(_habitRepo, _userDateService, _mediator);
        var skip = new SkipHabitTool(_mediator, _habitRepo);
        var handler = CreateHandler(RealAskTool(), query, update, skip);
        var held = HoldAskRerunWrites(query, update, skip);
        var captured = AskRerunCalls("compound-update-skip-en-r1")[0];
        var args = JsonNode.Parse(captured.Args.GetRawText())!.AsObject();
        args["question"] = "Which Walk would you like to skip?";
        args["options"] = JsonSerializer.SerializeToNode(walks.Select((walk, index) => new
        {
            id = walk.Id.ToString(),
            label = index == 0 ? "Morning walk" : "Evening walk"
        }));
        var ask = captured with { Args = nested ? JsonSerializer.SerializeToElement(new { input = args }) : JsonSerializer.SerializeToElement(args) };
        var writes = AskRerunWrites(water, walks[0]);
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = askFirst ? [ask, .. writes] : [.. writes, ask]
        });

        var result = await handler.Handle(new(UserId, $"Rename Water ({water.Id}) to Drink water and skip Walk today.",
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, PartKinds: ["choice"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().ContainSingle()
            .Which.EntityId.Should().Be(water.Id);
        held.Should().ContainSingle().Which.OperationId.Should().Be("update_habit");
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<AstraChoiceBlock>();
        await _blockStore.Received(1).AddAsync(Arg.Any<IssuedAstraBlock>(), Arg.Any<CancellationToken>());
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
        water.Title.Should().Be("Water");
    }

    private static AiToolCall[] AskRerunWrites(Habit water, Habit walk) =>
        AskRerunCalls("compound-update-skip-en-r2").Where(call => call.Name is "update_habit" or "skip_habit")
            .Select(call =>
            {
                var args = JsonNode.Parse(call.Args.GetRawText())!.AsObject();
                args["habit_id"] = (call.Name == "update_habit" ? water.Id : walk.Id).ToString();
                return call with { Id = "corrected-" + call.Id, Args = JsonSerializer.SerializeToElement(args) };
            }).ToArray();

    private List<AgentExecuteOperationRequest> HoldAskRerunWrites(QueryHabitsTool query, params IAiTool[] writes)
    {
        var blocks = UseProductionReadExecutor(query);
        var readExecutor = Orbit.Application.Tests.Chat.ReadBlockScopeTests.CreateExecutor(query, blocks);
        var registry = new AiToolRegistry(writes);
        var previewer = new PendingOperationChangePreviewer(_habitRepo, _goalRepo, _tagRepo, _userDateService, registry);
        var held = new List<AgentExecuteOperationRequest>();
        var stored = new Dictionary<Guid, PendingAgentOperationExecution>();
        _pendingOperationStore.GetExecution(UserId, Arg.Any<Guid>()).Returns(call => stored.GetValueOrDefault(call.ArgAt<Guid>(1)));
        _pendingOperationStore.Create(UserId, Arg.Any<AgentCapability>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), AgentExecutionSurface.Chat).Returns(call =>
                new PendingAgentOperation(Guid.NewGuid(), call.Arg<AgentCapability>().Id, "Review", "Review",
                    AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, DateTime.UtcNow.AddMinutes(10)));
        _pendingOperationStore.Revise(UserId, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var request = call.Arg<AgentExecuteOperationRequest>();
            if (request.OperationId == query.Name) return await readExecutor.ExecuteAsync(request, call.Arg<CancellationToken>());
            held.Add(request);
            var preview = (await previewer.PreviewAsync(UserId, request.OperationId, request.Arguments)).Value!;
            var pending = new PendingAgentOperation(Guid.NewGuid(), request.OperationId, request.OperationId, request.OperationId,
                AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, DateTime.UtcNow.AddMinutes(10),
                Changes: preview.Changes, ChangeTargetCount: preview.ChangeTargetCount, Items: preview.Items,
                PreviewFingerprint: preview.PreviewFingerprint);
            stored[pending.Id] = new(pending.Id, request.OperationId, request.OperationId, request.Arguments,
                AgentExecutionSurface.Chat, pending.ConfirmationRequirement, pending.PreviewFingerprint);
            return new AgentExecuteOperationResponse(new(request.OperationId, request.OperationId, AgentRiskClass.Low,
                pending.ConfirmationRequirement, AgentOperationStatus.PendingConfirmation), PendingOperation: pending);
        });
        return held;
    }
}
