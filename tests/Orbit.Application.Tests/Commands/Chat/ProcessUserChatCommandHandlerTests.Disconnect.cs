using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Fact]
    public async Task Handle_StreamDisconnectWithoutIdempotency_StillRecordsWithIndependentToken()
    {
        SetupUserAndPayGate();
        EnableRecording();
        const string reply = "Pronto.\n[[orbit:followups]]\nComo foi meu dia?\nQuais hábitos faltam?";
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                foreach (var character in reply)
                    await call.ArgAt<Func<AiStreamEvent, Task>>(1)(AiStreamEvent.Delta(character.ToString()));
                return Result.Success(new AiResponse { TextMessage = reply });
            });
        _conversationRecorder.RecordAsync(Arg.Any<ConversationTurnCapture>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return true;
            });
        using var disconnect = new CancellationTokenSource();
        var events = 0;
        var request = new ProcessUserChatCommand(UserId, "Help me plan", ClientContext: new AgentClientContext(SupportsFollowUps: true),
            StreamSink: _ =>
            {
                events++;
                disconnect.Cancel();
                return Task.CompletedTask;
            });
        var failure = await Xunit.Record.ExceptionAsync(async () => await CreateHandler().Handle(request, disconnect.Token));
        if (failure is not null) failure.Should().BeAssignableTo<OperationCanceledException>();
        disconnect.IsCancellationRequested.Should().BeTrue();
        events.Should().BeGreaterThan(0);
        await _conversationRecorder.Received(1).RecordAsync(Arg.Any<ConversationTurnCapture>(),
            Arg.Is<CancellationToken>(token => !token.CanBeCanceled));
    }
}
