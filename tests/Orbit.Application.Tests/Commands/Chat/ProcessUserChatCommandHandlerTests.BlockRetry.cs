using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    private IssuedAstraBlock RetryChoice() => RetryBlock(new("choice", "When?",
        [new("a", "Morning"), new("b", "Evening")], [], AllowFreeText: true));

    private IssuedAstraBlock RetryBlock(AstraBlockDefinition definition)
    {
        var block = IssuedAstraBlock.Create(UserId, definition);
        _unitOfWork.PassThroughTransactions<Result<PreparedAstraBlockAnswer>>();
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _blockStore.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (block.AnsweredAtUtc.HasValue)
                    return false;
                block.MarkAnswered(call.Arg<DateTime>());
                return true;
            });
        _blockStore.RecordAnswerAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<string>(),
                Arg.Any<DateOnly?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                block.RecordAnswer(System.Text.Json.JsonSerializer.Deserialize<AstraBlockAnswer>(call.ArgAt<string>(3))!, call.ArgAt<DateOnly?>(4));
                if (call.ArgAt<string?>(5) is { } response)
                    block.CompleteAnswer(response);
                return Task.CompletedTask;
            });
        _blockStore.CompleteAnswerAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => { block.CompleteAnswer(call.Arg<string>()); return true; });
        _blockStore.ReleaseAnswerAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(_ => { block.ReleaseAnswer(); return true; });
        return block;
    }

    [Fact]
    public async Task ChoiceAnswer_FailedAiReply_RetrySucceedsWithOneDailyCharge()
    {
        var user = User.Create("Alex", "retry@example.com").Value;
        var quota = CreateRealPayGate(user);
        var block = RetryChoice();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AiResponse>(ErrorMessages.AiUnavailable),
                Result.Success(new AiResponse { TextMessage = "Evening works." }));
        var handler = CreateHandler(quota);
        var request = new ProcessUserChatCommand(UserId, "Evening", BlockAnswer: new(block.Id, ["b"]));

        (await handler.Handle(request, default)).IsFailure.Should().BeTrue();
        user.GetAiMessagesUsedToday(Today).Should().Be(0);
        block.AnsweredAtUtc.Should().BeNull();
        var retry = await handler.Handle(request, default);

        retry.IsSuccess.Should().BeTrue();
        retry.Value.AiMessage.Should().Be("Evening works.");
        user.GetAiMessagesUsedToday(Today).Should().Be(1);
    }

    [Fact]
    public async Task ChoiceAnswer_LostReply_ReplaysStoredResponseAndRejectsDifferentAnswer()
    {
        var user = User.Create("Alex", "retry@example.com").Value;
        var quota = CreateRealPayGate(user);
        var block = RetryChoice();
        SetupAiResponse(new AiResponse { TextMessage = "Evening works." });
        var handler = CreateHandler(quota);
        var request = new ProcessUserChatCommand(UserId, "Evening", BlockAnswer: new(block.Id, ["b"], "After dinner"),
            ClientContext: new(SupportsParts: true));

        var first = await handler.Handle(request, default);
        var replay = await handler.Handle(request, default);

        replay.IsSuccess.Should().BeTrue();
        replay.Value.Should().BeEquivalentTo(first.Value);
        user.GetAiMessagesUsedToday(Today).Should().Be(1);
        (await handler.Handle(request with { BlockAnswer = new(block.Id, ["a"], "After dinner") }, default))
            .ErrorCode.Should().Be(ErrorCodes.AstraBlockAnswered);
        (await handler.Handle(request with { BlockAnswer = new(block.Id, ["b"], "Before dinner") }, default))
            .ErrorCode.Should().Be(ErrorCodes.AstraBlockAnswered);
        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Any<AiToolRequest>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChoiceAnswer_ReplayPreservesWireShapeOfNestedBlocks()
    {
        var user = User.Create("Alex", "retry@example.com").Value;
        var quota = CreateRealPayGate(user);
        var block = RetryChoice();
        SetupAiResponse(ToolResponse("ask", "followup", ChoiceArguments));
        var handler = CreateHandler(quota, RealAskTool());
        var request = new ProcessUserChatCommand(UserId, "Evening", BlockAnswer: new(block.Id, ["b"]),
            ClientContext: new(SupportsParts: true, PartKinds: ["choice"]));

        var first = await handler.Handle(request, default);
        var replay = await handler.Handle(request, default);

        first.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>();
        var firstJson = Orbit.Application.Chat.Models.ChatStreamEvent.Final(first.Value).ToJson();
        var replayJson = Orbit.Application.Chat.Models.ChatStreamEvent.Final(replay.Value).ToJson();
        System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(firstJson),
            System.Text.Json.Nodes.JsonNode.Parse(replayJson)).Should().BeTrue();
        user.GetAiMessagesUsedToday(Today).Should().Be(1);
        await _blockStore.Received(1).AddAsync(Arg.Any<IssuedAstraBlock>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("continuation")]
    [InlineData("tool")]
    [InlineData("iterations")]
    [InlineData("budget")]
    public async Task ChoiceAnswer_FailedToolLoop_ReopensAndRefunds(string failure)
    {
        var user = User.Create("Alex", "retry@example.com").Value;
        var quota = CreateRealPayGate(user);
        var block = RetryChoice();
        var tool = ReadTool("describe_feature", new ToolResult(failure != "tool"));
        var handler = CreateHandler(quota, tool);
        var toolResponse = ToolResponse("describe_feature", "read", "{}");
        SetupAiResponse(failure == "budget"
            ? new AiResponse { ReportedTokenCount = AppConstants.MaxAiRequestTotalTokens + 1 }
            : toolResponse);
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(failure == "iterations" ? Result.Success(toolResponse) : Result.Failure<AiResponse>(ErrorMessages.AiUnavailable));
        var request = new ProcessUserChatCommand(UserId, "Evening", BlockAnswer: new(block.Id, ["b"]));

        (await handler.Handle(request, default)).IsFailure.Should().BeTrue();
        block.AnsweredAtUtc.Should().BeNull();
        user.GetAiMessagesUsedToday(Today).Should().Be(0);
        SetupAiResponse(new AiResponse { TextMessage = "Evening works." });
        (await handler.Handle(request, default)).IsSuccess.Should().BeTrue();
        user.GetAiMessagesUsedToday(Today).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChoiceAnswer_ThrownOrCancelledAiCall_CompensatesBeforeRetry(bool cancel)
    {
        var user = User.Create("Alex", "retry@example.com").Value;
        var quota = CreateRealPayGate(user);
        var block = RetryChoice();
        using var cancellation = new CancellationTokenSource();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (!cancel)
                    return Task.FromException<Result<AiResponse>>(new InvalidOperationException("AI unavailable"));
                cancellation.Cancel();
                return Task.FromException<Result<AiResponse>>(new OperationCanceledException(cancellation.Token));
            });
        var handler = CreateHandler(quota);
        var request = new ProcessUserChatCommand(UserId, "Evening", BlockAnswer: new(block.Id, ["b"]));

        Func<Task> answer = () => handler.Handle(request, cancellation.Token);
        if (cancel)
            await answer.Should().ThrowAsync<OperationCanceledException>();
        else
            await answer.Should().ThrowAsync<InvalidOperationException>();
        block.AnsweredAtUtc.Should().BeNull();
        user.GetAiMessagesUsedToday(Today).Should().Be(0);
        await _blockStore.Received(1).ReleaseAnswerAsync(block.Id, UserId, Arg.Any<DateTime>(),
            Arg.Is<CancellationToken>(token => !token.IsCancellationRequested));

        SetupAiResponse(new AiResponse { TextMessage = "Evening works." });
        (await handler.Handle(request, default)).IsSuccess.Should().BeTrue();
        user.GetAiMessagesUsedToday(Today).Should().Be(1);
    }

    [Fact]
    public async Task ChoiceAnswer_ConcurrentIdenticalAnswers_ChargeOnceAndReplayAfterCompletion()
    {
        var user = User.Create("Alex", "retry@example.com").Value;
        var quota = CreateRealPayGate(user);
        var block = RetryChoice();
        using var work = new SerializingUnitOfWork();
        var enteredAi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishAi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                enteredAi.SetResult();
                await finishAi.Task;
                return Result.Success(new AiResponse { TextMessage = "Evening works." });
            });
        var handler = CreateHandler(_userRepo, work, quota);
        var request = new ProcessUserChatCommand(UserId, "Evening", BlockAnswer: new(block.Id, ["b"]));
        var firstTask = handler.Handle(request, default);
        await enteredAi.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var concurrent = await handler.Handle(request, default);
        finishAi.SetResult();
        var first = await firstTask;
        var replay = await handler.Handle(request, default);

        first.IsSuccess.Should().BeTrue();
        concurrent.ErrorCode.Should().Be(ErrorCodes.AstraBlockAnswered);
        replay.Value.Should().BeEquivalentTo(first.Value);
        user.GetAiMessagesUsedToday(Today).Should().Be(1);
        work.Order.Where(item => item.StartsWith("lock:astra-blocks:", StringComparison.Ordinal))
            .Should().HaveCount(4).And.OnlyContain(item => item == $"lock:astra-blocks:{UserId}");
        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Any<AiToolRequest>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PickListAnswer_FailedPreview_RetryReplaysSamePendingOperationWithoutCharge()
    {
        SetupUserAndPayGate(payGatePass: false);
        var block = RetryBlock(new("pickList", "Choose", [],
            [new("a", "Walk", ParseArguments("{\"title\":\"Walk\"}")),
                new("b", "Read", ParseArguments("{\"title\":\"Read\"}"))], true, Action: "create_habits"));
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<PreparedAstraBlockAnswer>>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                try
                {
                    return await call.Arg<Func<CancellationToken, Task<Result<PreparedAstraBlockAnswer>>>>()(default);
                }
                catch
                {
                    block.ReleaseAnswer();
                    throw;
                }
            });
        var handler = CreateHandler();
        var pending = new PendingAgentOperation(Guid.NewGuid(), "habits", "Habits", "Preview", AgentRiskClass.Low,
            AgentConfirmationRequirement.FreshConfirmation, block.ExpiresAtUtc,
            Items: [new("a", null, "Walk", [], "fingerprint")]);
        var operation = new AgentOperationResult("bulk_create_habits", "bulk_create_habits", AgentRiskClass.Low,
            AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation);
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(operation), new AgentExecuteOperationResponse(operation, pending));
        _pendingOperationStore.IsAwaitingConfirmation(UserId, pending.Id).Returns(true);
        var request = new ProcessUserChatCommand(UserId, "Walk", BlockAnswer: new(block.Id, ["a"]),
            ClientContext: new(SupportsParts: true));

        (await handler.Handle(request, default)).ErrorCode.Should().Be(ErrorCodes.AstraBlockPreviewUnavailable);
        block.AnsweredAtUtc.Should().BeNull();
        var first = await handler.Handle(request, default);
        var replay = await handler.Handle(request, default);
        first.IsSuccess.Should().BeTrue();
        replay.Value.Should().BeEquivalentTo(first.Value);
        replay.Value.PendingOperations.Should().ContainSingle().Which.Id.Should().Be(pending.Id);
        (await handler.Handle(request with { BlockAnswer = new(block.Id, ["b"]) }, default))
            .ErrorCode.Should().Be(ErrorCodes.AstraBlockAnswered);

        _pendingOperationStore.IsAwaitingConfirmation(UserId, pending.Id).Returns(false);
        var closed = await handler.Handle(request, default);
        closed.IsSuccess.Should().BeTrue();
        closed.Value.PendingOperations.Should().BeEmpty();
        closed.Value.AiMessage.Should().Be(first.Value.AiMessage);
        await _operationExecutor.Received(2).ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>());
        await _aiIntentService.DidNotReceiveWithAnyArgs().SendWithToolsAsync(default!, default, default);
        await _payGate.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
    }
}
