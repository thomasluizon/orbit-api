using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task EmptyProviderCompletion_RecoversInitialAndReadContinuationWithoutAnotherCall(string language, bool continuation)
    {
        var user = User.Create("Person", "empty@example.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        var tool = ReadTool("query_habits", new ToolResult(true, Payload: new { habits = Array.Empty<object>() }));
        if (continuation)
        {
            SetupAiResponse(ToolResponse(tool.Name, "empty_read", "{}"));
            _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
                .Returns(Result.Failure<AiResponse>(ErrorMessages.AiEmptyResponse));
        }
        else
            _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
                .Returns(Result.Failure<AiResponse>(ErrorMessages.AiEmptyResponse));
        var events = new List<ChatStreamEvent>();

        var result = await CreateHandler(tool).Handle(new(UserId, "Show my habits", CorrelationId: "empty-recovery",
            ClientContext: new(SupportsParts: true, SupportsMessageAllowance: true),
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be(language == "pt-BR"
            ? "Não consegui preparar uma resposta. Tente novamente."
            : "I couldn't prepare an answer. Please try again.");
        result.Value.Parts.Should().ContainSingle().Which.Should().Be(new ChatTextPart(result.Value.AiMessage!));
        result.Value.CorrelationId.Should().Be("empty-recovery");
        result.Value.MessageAllowanceConsumed.Should().BeTrue();
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        events.TakeLast(2).Should().Equal(ChatStreamEvent.Reset(), ChatStreamEvent.Delta(result.Value.AiMessage!));
        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        await _aiIntentService.ReceivedWithAnyArgs(continuation ? 1 : 0).ContinueWithToolResultsAsync(default!, default!, default, default);
        await _payGate.Received(1).TryConsumeAiMessage(UserId, _unitOfWork, Arg.Any<CancellationToken>());
    }
}
