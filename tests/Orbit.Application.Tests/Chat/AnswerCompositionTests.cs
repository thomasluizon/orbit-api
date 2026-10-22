using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    private void EnableComposition() => _featureFlagService
        .GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
        .Returns(["astra_block_first", "astra_conversation_recording"]);

    [Theory]
    [InlineData("Here are your habits.\n[[orbit:read:answer]]")]
    [InlineData("Here are your habits.")]
    public async Task Composition_LeadsWithTypedReadDespiteModelPositionOrOmission(string reply)
    {
        var result = await ComposeHabitAnswer(reply);

        result.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Kind.Should().Be("habitList");
        result.AiMessage.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Composition_SuppressesRepeatedFiguresAndFacts()
    {
        var result = await ComposeHabitAnswer("You have 0 habits.\n[[orbit:read:answer]]\nThere are zero habits today.");

        result.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>();
        result.AiMessage.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Composition_ReducesThreeFollowUpsWithoutAnotherCall()
    {
        var result = await ComposeHabitAnswer("[[orbit:read:answer]]\n[[orbit:followups]]\nShow my habits?\nWhat habits remain?\nShow all habits?");

        result.FollowUps.Should().Equal("Show my habits?", "What habits remain?");
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Composition_WithholdsPreBlockDeltas()
    {
        var events = new List<ChatStreamEvent>();
        var result = await ComposeHabitAnswer("You have 0 habits.\n[[orbit:read:answer]]", events);

        events.Where(item => item.Type == "delta").Should().BeEmpty();
        events.Add(ChatStreamEvent.Final(result));
        events.Last().Response!.Parts![0].Should().BeOfType<ChatBlockPart>();
        _conversationRecorder.Received(1).TryRecord(Arg.Is<ConversationTurnCapture>(turn => turn.AssistantContent == result.AiMessage));
    }

    private async Task<ChatResponse> ComposeHabitAnswer(string reply, List<ChatStreamEvent>? events = null)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "answer", "{}"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (call.ArgAt<Func<AiStreamEvent, Task>?>(2) is { } sink)
                    foreach (var character in reply)
                        await sink(AiStreamEvent.Delta(character.ToString()));
                return Result.Success(new AiResponse { TextMessage = reply });
            });
        var result = await handler.Handle(new(UserId, "Show my habits", ClientContext: new(
            SupportsParts: true, PartKinds: ["habitList"], SupportsHabitListCard: true, SupportsFollowUps: true),
            StreamSink: events is null ? null : item => { events.Add(item); return Task.CompletedTask; }), default);
        result.IsSuccess.Should().BeTrue();
        return result.Value;
    }
}
