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
        _unitOfWork.PassThroughTransactions<ChatResponse?>();
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _blockStore.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var claim = call.Arg<DateTime>();
                if (block.AnsweredAtUtc.HasValue || block.InvalidatedAtUtc.HasValue || block.ExpiresAtUtc <= claim)
                    return false;
                SetBlock(block, nameof(block.AnsweredAtUtc), claim);
                return true;
            });
        _blockStore.RecordAnswerAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<string>(),
                Arg.Any<DateOnly?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (!UnfinishedBlock(block, call.ArgAt<DateTime>(2)))
                    throw new InvalidOperationException("Block answer reservation was lost.");
                SetBlock(block, nameof(block.AnswerJson), call.ArgAt<string>(3));
                SetBlock(block, nameof(block.ReservedAllowanceDate), call.ArgAt<DateOnly?>(4));
                SetBlock(block, nameof(block.ResponseJson), call.ArgAt<string?>(5));
                return Task.CompletedTask;
            });
        _blockStore.CompleteAnswerAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (!UnfinishedBlock(block, call.Arg<DateTime>())) return false;
                SetBlock(block, nameof(block.ResponseJson), call.Arg<string>());
                return true;
            });
        _blockStore.CheckpointAnswerAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (!UnfinishedBlock(block, call.Arg<DateTime>())) return false;
                SetBlock(block, nameof(block.CommittedResponseJson), call.Arg<string>());
                return true;
            });
        _blockStore.ReleaseAnswerAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (!UnfinishedBlock(block, call.Arg<DateTime>()) || block.CommittedResponseJson is not null) return false;
                ClearBlockAnswer(block);
                return true;
            });
        return block;
    }

    private static bool UnfinishedBlock(IssuedAstraBlock block, DateTime claim) =>
        block.AnsweredAtUtc == claim && block.ResponseJson is null;

    private static void SetBlock(IssuedAstraBlock block, string name, object? value) =>
        typeof(IssuedAstraBlock).GetProperty(name)!.SetValue(block, value);

    private static void ClearBlockAnswer(IssuedAstraBlock block)
    {
        SetBlock(block, nameof(block.AnsweredAtUtc), null);
        SetBlock(block, nameof(block.AnswerJson), null);
        SetBlock(block, nameof(block.ReservedAllowanceDate), null);
    }

    [Theory]
    [InlineData("continuation", "en")]
    [InlineData("cancel", "en")]
    [InlineData("budget", "en")]
    [InlineData("completion", "en")]
    [InlineData("serialization", "en")]
    [InlineData("continuation", "pt-BR")]
    public async Task ChoiceAnswer_CommittedHabitThenFailure_ReplaysWithoutCreatingAgain(string failure, string language)
    {
        var user = User.Create("Alex", "retry@example.com").Value;
        user.SetLanguage(language);
        var quota = CreateRealPayGate(user);
        var block = RetryChoice();
        _payGate.CanCreateHabits(UserId, 1, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _unitOfWork.PassThroughTransactions<ToolResult>();
        var createHabit = new Orbit.Application.Chat.Tools.Implementations.CreateHabitTool(
            _habitRepo, _tagRepo, _goalRepo, _userDateService, _payGate, _unitOfWork);
        IAiTool tool = createHabit;
        if (failure == "serialization")
        {
            tool = Substitute.For<IAiTool>();
            tool.Name.Returns("create_habit");
            tool.ExecuteAsync(Arg.Any<System.Text.Json.JsonElement>(), UserId, Arg.Any<CancellationToken>())
                .Returns(async call => (await createHabit.ExecuteAsync(call.Arg<System.Text.Json.JsonElement>(),
                    UserId, call.Arg<CancellationToken>())) with
                { Payload = typeof(Habit) });
        }
        if (failure == "completion")
        {
            var attempts = 0;
            _blockStore.CompleteAnswerAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    if (++attempts == 1) throw new InvalidOperationException("completion failed");
                    if (!UnfinishedBlock(block, call.Arg<DateTime>())) return false;
                    SetBlock(block, nameof(block.ResponseJson), call.Arg<string>());
                    return true;
                });
        }
        var handler = CreateHandler(quota, tool);
        SetupAiResponse(ToolResponse("create_habit", "write", "{\"title\":\"Walk\",\"frequency_unit\":\"Day\"}"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => failure == "cancel"
                ? Task.FromException<Result<AiResponse>>(new OperationCanceledException())
                : Task.FromResult(failure == "budget"
                    ? Result.Success(new AiResponse { ReportedTokenCount = AppConstants.MaxAiRequestTotalTokens + 1 })
                    : failure is "completion" or "serialization"
                        ? Result.Success(new AiResponse { TextMessage = "Created." })
                        : Result.Failure<AiResponse>(ErrorMessages.AiUnavailable)));
        var request = new ProcessUserChatCommand(UserId, "Evening", BlockAnswer: new(block.Id, ["b"]));

        var first = await handler.Handle(request, default);
        await _habitRepo.Received(1).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        var retry = await handler.Handle(request, default);

        first.IsSuccess.Should().BeTrue();
        first.Value.AiMessage.Should().Be(language == "pt-BR"
            ? "Salvei as mudanças listadas aqui, mas não consegui terminar minha resposta."
            : "I saved the changes listed here, but I couldn't finish my reply.");
        first.Value.Actions.Should().ContainSingle(action => action.Type == "CreateHabit" && action.Status == ActionStatus.Success);
        retry.Value.Should().BeEquivalentTo(first.Value);
        user.GetAiMessagesUsedToday(Today).Should().Be(1);
        await _habitRepo.Received(1).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", "I couldn't finish my reply, and nothing changes until you approve.")]
    [InlineData("pt-BR", "Não consegui terminar minha resposta, e nada muda até você aprovar.")]
    public async Task ChoiceAnswer_PendingWriteThenFailure_ReplaysWithoutClaimingChangesWereSaved(string language, string message)
    {
        var user = User.Create("Alex", "retry@example.com").Value;
        user.SetLanguage(language);
        var quota = CreateRealPayGate(user);
        var block = RetryChoice();
        var tool = FakeTool("delete_habit");
        tool.IsReadOnly.Returns(false);
        var handler = CreateHandler(quota, tool);
        var pending = new PendingAgentOperation(Guid.NewGuid(), "habits", "Habits", "Delete",
            AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, block.ExpiresAtUtc);
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new("delete_habit", "delete_habit", AgentRiskClass.Low,
                AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation), pending));
        _pendingOperationStore.IsAwaitingConfirmation(UserId, pending.Id).Returns(true);
        _pendingOperationStore.GetExecution(UserId, pending.Id).Returns(new PendingAgentOperationExecution(pending.Id,
            pending.CapabilityId, "delete_habit", ParseArguments("{}"), AgentExecutionSurface.Chat, pending.ConfirmationRequirement));
        SetupAiResponse(ToolResponse("delete_habit", "write", "{}"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Failure<AiResponse>(ErrorMessages.AiUnavailable));
        var request = new ProcessUserChatCommand(UserId, "Evening", BlockAnswer: new(block.Id, ["b"]));

        var first = await handler.Handle(request, default);
        var replay = await handler.Handle(request, default);

        first.IsSuccess.Should().BeTrue();
        first.Value.AiMessage.Should().Be(message);
        first.Value.Actions.Should().BeEmpty();
        replay.Value.Should().BeEquivalentTo(first.Value);
        replay.Value.PendingOperations.Should().ContainSingle().Which.Id.Should().Be(pending.Id);
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
    public async Task ChoiceAnswer_CompensationThrows_PreservesOriginalExceptionAndLogs()
    {
        var user = User.Create("Alex", "retry@example.com").Value;
        var quota = CreateRealPayGate(user);
        var block = RetryChoice();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result<AiResponse>>(new InvalidOperationException("original failure")));
        _blockStore.ReleaseAnswerAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new InvalidOperationException("compensation failure")));
        var handler = CreateHandler(quota);

        var action = () => handler.Handle(new(UserId, "Evening", BlockAnswer: new(block.Id, ["b"])), default);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("original failure");
        _logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "Log")
            .SelectMany(call => call.GetArguments()).OfType<InvalidOperationException>()
            .Should().ContainSingle(exception => exception.Message == "compensation failure");
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
                    ClearBlockAnswer(block);
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
        _pendingOperationStore.GetExecution(UserId, pending.Id).Returns(new PendingAgentOperationExecution(pending.Id,
            pending.CapabilityId, "bulk_create_habits", ParseArguments("""{"habits":[{"title":"Walk"}]}"""),
            AgentExecutionSurface.Chat, pending.ConfirmationRequirement));
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
