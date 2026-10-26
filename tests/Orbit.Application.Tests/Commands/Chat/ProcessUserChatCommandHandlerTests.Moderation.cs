using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task Moderation_OverlapsContextAndGatesModel(bool flagged, bool unavailable, bool blockAnswer)
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = "Allowed" });
        AstraBlockAnswer? answer = null;
        if (blockAnswer)
        {
            _unitOfWork.PassThroughTransactions<Result<PreparedAstraBlockAnswer>>();
            var block = IssuedAstraBlock.Create(UserId, new("choice", "When?", [new("a", "Morning"), new("b", "Evening")], []));
            answer = new(block.Id, ["b"]);
            _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
            _blockStore.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        }
        var check = new TaskCompletionSource<ModerationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var contextStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new TaskCompletionSource<IReadOnlyList<Habit>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(check.Task);
        _habitRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var token = call.Arg<CancellationToken>();
                contextStarted.TrySetResult(token);
                return context.Task.WaitAsync(token);
            });

        using var cancellation = new CancellationTokenSource();
        var turn = CreateHandler().Handle(new(UserId, "Hello", ClientContext: new(SupportsMessageAllowance: true), BlockAnswer: answer), cancellation.Token);
        try
        {
            var token = await contextStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            _aiIntentService.ReceivedCalls().Should().BeEmpty();
            check.SetResult(new ModerationResult(flagged, unavailable, []));
            if (!flagged && !unavailable)
            {
                _aiIntentService.ReceivedCalls().Should().BeEmpty();
                context.SetResult([]);
            }
            var result = await turn.WaitAsync(TimeSpan.FromSeconds(2));
            if (flagged || unavailable)
            {
                token.IsCancellationRequested.Should().BeTrue();
                _aiIntentService.ReceivedCalls().Should().BeEmpty();
                _payGate.ReceivedCalls().Should().BeEmpty();
                await _blockStore.DidNotReceiveWithAnyArgs().InvalidateOpenAsync(default, default);
                result.IsFailure.Should().Be(unavailable);
            }
            else
            {
                result.IsSuccess.Should().BeTrue();
                await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Any<AiToolRequest>(),
                    Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
                await _habitRepo.Received(1).FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(),
                    Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>());
            }
        }
        finally
        {
            check.TrySetResult(new ModerationResult(false, true, []));
            context.TrySetResult([]);
            cancellation.Cancel();
            await turn;
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(null, true)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Moderation_Unavailable_StillFailsClosed(bool? supportsAllowance, bool stream)
    {
        SetupUserAndPayGate();
        EnableRecording();
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(false, true, []));
        var streamed = new List<ChatStreamEvent>();

        var result = await CreateHandler().Handle(new(UserId, "screen this input",
            ClientContext: supportsAllowance is null ? null : new(SupportsMessageAllowance: supportsAllowance),
            StreamSink: stream ? item => { streamed.Add(item); return Task.CompletedTask; }
        : null), default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.AiUnavailable);
        streamed.Should().BeEmpty();
        _conversationRecorder.DidNotReceiveWithAnyArgs().TryRecord(default!);
        _payGate.ReceivedCalls().Should().BeEmpty();
        _aiIntentService.ReceivedCalls().Should().BeEmpty();
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task Moderation_LegacyClient_ReturnsLocalizedTypedError(string language, bool stream)
    {
        var user = User.Create("Alex", "alex@test.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableRecording();
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(true, false, ["violence"]));
        var streamed = new List<ChatStreamEvent>();

        var result = await CreateHandler().Handle(new(UserId, "screen this input",
            StreamSink: stream ? item => { streamed.Add(item); return Task.CompletedTask; }
        : null), default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ContentModerationRefused);
        result.Error.Should().Be(language == "pt-BR"
            ? "Não posso ajudar com isso, mas posso ajudar com seus hábitos e metas."
            : "I can't help with that, but I can help with your habits and goals.");
        streamed.Should().BeEmpty();
        _conversationRecorder.DidNotReceiveWithAnyArgs().TryRecord(default!);
        _payGate.ReceivedCalls().Should().BeEmpty();
        _aiIntentService.ReceivedCalls().Should().BeEmpty();
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Theory]
    [InlineData("en", null, false)]
    [InlineData("pt-BR", null, false)]
    [InlineData("en", null, true)]
    [InlineData("pt-BR", null, true)]
    [InlineData("en", "choice", false)]
    [InlineData("pt-BR", "choice", false)]
    [InlineData("en", "choice", true)]
    [InlineData("pt-BR", "choice", true)]
    [InlineData("en", "pickList", false)]
    [InlineData("pt-BR", "pickList", false)]
    [InlineData("en", "pickList", true)]
    [InlineData("pt-BR", "pickList", true)]
    public async Task Moderation_Flagged_ReturnsLocalizedReplyWithoutSideEffects(string language, string? form, bool stream)
    {
        var user = User.Create("Alex", "alex@test.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user, payGatePass: false);
        EnableRecording();
        _unitOfWork.PassThroughTransactions<Result<PreparedAstraBlockAnswer>>();
        var definition = form == "pickList"
            ? new AstraBlockDefinition("pickList", "Choose", [], [new("b", "Read", ParseArguments("{\"title\":\"Read\"}"))],
                MultiSelect: true, Action: "create_habits")
            : new AstraBlockDefinition("choice", "When?", [new("a", "Morning"), new("b", "Evening")], []);
        var block = IssuedAstraBlock.Create(UserId, definition);
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(true, false, []));
        var streamed = new List<ChatStreamEvent>();
        var history = new List<ChatHistoryMessage> { new("user", "Earlier message") };
        var request = new ProcessUserChatCommand(UserId, "screen this input", History: history,
            ClientContext: new AgentClientContext(SupportsParts: true, SupportsMessageAllowance: true), CorrelationId: "moderated-turn",
            StreamSink: stream ? item => { streamed.Add(item); return Task.CompletedTask; }
        : null,
            BlockAnswer: form is not null ? new(block.Id, ["b"]) : null);

        var result = await CreateHandler().Handle(request, default);

        result.IsSuccess.Should().BeTrue();
        result.Value.MessageAllowanceConsumed.Should().BeFalse();
        var expected = language == "pt-BR"
            ? "Não posso ajudar com isso, mas posso ajudar com seus hábitos e metas."
            : "I can't help with that, but I can help with your habits and goals.";
        result.Value.AiMessage.Should().Be(expected);
        result.Value.CorrelationId.Should().Be("moderated-turn");
        result.Value.Actions.Should().BeEmpty();
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.Operations.Should().BeNullOrEmpty();
        result.Value.Parts.Should().Equal(new ChatTextPart(expected));
        history.Should().ContainSingle().Which.Content.Should().Be("Earlier message");
        if (stream)
            streamed.Should().Equal(ChatStreamEvent.Reset(), ChatStreamEvent.Delta(expected));
        else
            streamed.Should().BeEmpty();
        await _moderation.Received(1).CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _conversationRecorder.DidNotReceiveWithAnyArgs().TryRecord(default!);
        _payGate.ReceivedCalls().Should().BeEmpty();
        _scopeFactory.ReceivedCalls().Should().BeEmpty();
        _aiIntentService.ReceivedCalls().Should().BeEmpty();
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
        await _blockStore.DidNotReceiveWithAnyArgs().InvalidateOpenAsync(default, default);
        await _blockStore.DidNotReceiveWithAnyArgs().MarkAnsweredAsync(default, default, default, default);
        _pendingOperationStore.ReceivedCalls().Should().BeEmpty();
        _userRepo.ReceivedCalls().Should().OnlyContain(call => call.GetMethodInfo().Name == nameof(_userRepo.GetByIdAsync));
        _habitRepo.ReceivedCalls().Should().BeEmpty();
        _goalRepo.ReceivedCalls().Should().BeEmpty();
    }

    public static TheoryData<string, string, string?, bool, bool> SelfHarmModerationCases
    {
        get
        {
            var cases = new TheoryData<string, string, string?, bool, bool>();
            foreach (var category in new[] { "self-harm", "self-harm/intent", "self-harm/instructions" })
                foreach (var language in new[] { "en", "pt-BR" })
                    foreach (var form in new string?[] { null, "choice", "pickList" })
                        foreach (var stream in new[] { false, true })
                            foreach (var supportsAllowance in new[] { false, true })
                                cases.Add(category, language, form, stream, supportsAllowance);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(SelfHarmModerationCases))]
    public async Task Moderation_SelfHarmCategory_ReturnsCrisisReplyWithoutSideEffects(
        string category, string language, string? form, bool stream, bool supportsAllowance)
    {
        var user = User.Create("Alex", "alex@test.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user, payGatePass: false);
        EnableRecording();
        _unitOfWork.PassThroughTransactions<Result<PreparedAstraBlockAnswer>>();
        var definition = form == "pickList"
            ? new AstraBlockDefinition("pickList", "Choose", [], [new("b", "Read", ParseArguments("{\"title\":\"Read\"}"))],
                MultiSelect: true, Action: "create_habits")
            : new AstraBlockDefinition("choice", "When?", [new("a", "Morning"), new("b", "Evening")], []);
        var block = IssuedAstraBlock.Create(UserId, definition);
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(true, false, [category]));
        var streamed = new List<ChatStreamEvent>();
        const string message = "I feel like disappearing forever";
        CrisisSupportGuard.Detect(message).Should().Be(CrisisLocales.None);

        var result = await CreateHandler().Handle(new(UserId, message,
            ClientContext: new(SupportsMessageAllowance: supportsAllowance),
            CorrelationId: "moderated-crisis",
            StreamSink: stream ? item => { streamed.Add(item); return Task.CompletedTask; }
        : null,
            BlockAnswer: form is not null ? new(block.Id, ["b"]) : null), default);

        var locales = language == "pt-BR" ? CrisisLocales.Portuguese : CrisisLocales.English;
        var expected = CrisisSupportGuard.EnsureResources(CrisisSupportGuard.FallbackMessage(locales), locales, null);
        result.IsSuccess.Should().BeTrue();
        result.Value.MessageAllowanceConsumed.Should().Be(supportsAllowance ? false : null);
        result.Value.AiMessage.Should().Be(expected);
        result.Value.CorrelationId.Should().Be("moderated-crisis");
        result.Value.Actions.Should().BeEmpty();
        result.Value.Operations.Should().BeNullOrEmpty();
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        if (stream)
            streamed.Should().Equal(ChatStreamEvent.Reset(), ChatStreamEvent.Delta(expected));
        else
            streamed.Should().BeEmpty();
        await _moderation.Received(1).CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _conversationRecorder.DidNotReceiveWithAnyArgs().TryRecord(default!);
        _payGate.ReceivedCalls().Should().BeEmpty();
        _scopeFactory.ReceivedCalls().Should().BeEmpty();
        _aiIntentService.ReceivedCalls().Should().BeEmpty();
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
        await _blockStore.DidNotReceiveWithAnyArgs().InvalidateOpenAsync(default, default);
        await _blockStore.DidNotReceiveWithAnyArgs().MarkAnsweredAsync(default, default, default, default);
        _pendingOperationStore.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Moderation_NonSelfHarmCategory_ReturnsNeutralDecline()
    {
        SetupUserAndPayGate();
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(true, false, ["violence"]));

        var result = await CreateHandler().Handle(new(UserId, "screen this input",
            ClientContext: new(SupportsMessageAllowance: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("I can't help with that, but I can help with your habits and goals.");
        _aiIntentService.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Moderation_PhraseGuardAndCategoryMatch_ReturnsSingleCrisisReply(bool supportsAllowance, bool stream)
    {
        SetupUserAndPayGate();
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(true, false, ["self-harm/intent"]));

        var streamed = new List<ChatStreamEvent>();
        var result = await CreateHandler().Handle(new(UserId, "I want to hurt myself",
            ClientContext: new(SupportsMessageAllowance: supportsAllowance),
            StreamSink: stream ? item => { streamed.Add(item); return Task.CompletedTask; }
        : null), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.MessageAllowanceConsumed.Should().Be(supportsAllowance ? false : null);
        result.Value.AiMessage.Should().Be(CrisisSupportGuard.EnglishSupport + "\n\n" + CrisisSupportGuard.EnglishResource);
        if (stream)
            streamed.Should().Equal(ChatStreamEvent.Reset(), ChatStreamEvent.Delta(result.Value.AiMessage));
        else
            streamed.Should().BeEmpty();
        await _moderation.DidNotReceiveWithAnyArgs().CheckTextAsync(default!, default);
        _aiIntentService.ReceivedCalls().Should().BeEmpty();
        _payGate.ReceivedCalls().Should().BeEmpty();
    }

    public static TheoryData<string, string, bool, bool> RemainingMessageCases
    {
        get
        {
            var cases = new TheoryData<string, string, bool, bool>();
            foreach (var (message, category) in new[] { ("screen this input", "violence"),
                ("I feel like disappearing forever", "self-harm/intent"), ("I want to hurt myself", "self-harm/intent") })
                foreach (var supportsAllowance in new[] { false, true })
                    foreach (var stream in new[] { false, true })
                        cases.Add(message, category, supportsAllowance, stream);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(RemainingMessageCases))]
    public async Task Moderation_OneServerMessageRemaining_NextBenignMessageIsAccepted(
        string message, string category, bool supportsAllowance, bool stream)
    {
        var user = User.Create("Alex", "alex@test.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        for (var i = 0; i < 4; i++)
            user.IncrementAiMessageCount(Today);
        var handler = CreateHandler(CreateRealPayGate(user));
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(true, false, [category]));

        var request = new ProcessUserChatCommand(UserId, message,
            ClientContext: new(SupportsMessageAllowance: supportsAllowance),
            StreamSink: stream ? _ => Task.CompletedTask : null);
        var declined = await handler.Handle(request, default);

        if (supportsAllowance || category != "violence")
        {
            declined.IsSuccess.Should().BeTrue();
            declined.Value.MessageAllowanceConsumed.Should().Be(supportsAllowance ? false : null);
        }
        else
        {
            declined.IsFailure.Should().BeTrue();
            declined.ErrorCode.Should().Be(ErrorCodes.ContentModerationRefused);
        }
        user.AiMessagesUsedToday.Should().Be(4);
        _aiIntentService.ReceivedCalls().Should().BeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(false, false, []));
        SetupAiResponse(new AiResponse { TextMessage = "Allowed" });

        var next = await handler.Handle(request with { Message = "Hello" }, default);

        next.IsSuccess.Should().BeTrue();
        next.Value.AiMessage.Should().Be("Allowed");
        next.Value.MessageAllowanceConsumed.Should().Be(supportsAllowance ? true : null);
        user.AiMessagesUsedToday.Should().Be(5);
        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Moderation_Clean_KeepsNormalTurnAndAllowance(bool? supportsAllowance)
    {
        SetupUserAndPayGate();
        EnableRecording();
        SetupAiResponse(new AiResponse { TextMessage = "Allowed" });

        var result = await CreateHandler().Handle(new(UserId, "Hello",
            ClientContext: supportsAllowance is null ? null : new(SupportsMessageAllowance: supportsAllowance)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("Allowed");
        result.Value.MessageAllowanceConsumed.Should().Be(supportsAllowance == true ? true : null);
        await _moderation.Received(1).CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _payGate.Received(1).TryConsumeAiMessage(UserId, _unitOfWork, Arg.Any<CancellationToken>());
        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        _conversationRecorder.Received(1).TryRecord(Arg.Any<ConversationTurnCapture>());
    }
}
