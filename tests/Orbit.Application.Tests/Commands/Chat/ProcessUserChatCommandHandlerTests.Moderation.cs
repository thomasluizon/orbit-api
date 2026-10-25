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
            ClientContext: new AgentClientContext(SupportsParts: true), CorrelationId: "moderated-turn",
            StreamSink: stream ? item => { streamed.Add(item); return Task.CompletedTask; }
        : null,
            BlockAnswer: form is not null ? new(block.Id, ["b"]) : null);

        var result = await CreateHandler().Handle(request, default);

        result.IsSuccess.Should().BeTrue();
        var expected = language == "pt-BR"
            ? "Não posso ajudar a machucar você ou outras pessoas, nem a esconder isso. Posso ajudar você a se afastar de um conflito e pensar em um próximo passo seguro."
            : "I can't help with harming yourself or others, or hiding it. I can help you step away from a conflict and plan a safe next step.";
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

    [Fact]
    public async Task Moderation_Clean_KeepsNormalTurnAndAllowance()
    {
        SetupUserAndPayGate();
        EnableRecording();
        SetupAiResponse(new AiResponse { TextMessage = "Allowed" });

        var result = await CreateHandler().Handle(new(UserId, "Hello"), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("Allowed");
        await _moderation.Received(1).CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _payGate.Received(1).TryConsumeAiMessage(UserId, _unitOfWork, Arg.Any<CancellationToken>());
        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        _conversationRecorder.Received(1).TryRecord(Arg.Any<ConversationTurnCapture>());
    }
}
