using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Models;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Tests.Common;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Fact]
    public async Task ConversationRecorder_Throwing_DoesNotChangeReplyOrStream()
    {
        SetupUserAndPayGate();
        EnableRecording();
        _conversationRecorder.TryRecord(Arg.Any<ConversationTurnCapture>())
            .Returns(_ => throw new InvalidOperationException("Storage unavailable"));
        SetupAiResponse(new AiResponse { TextMessage = "Your reply" });
        var streamed = new List<Orbit.Application.Chat.Models.ChatStreamEvent>();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.Arg<Func<AiStreamEvent, Task>>()(AiStreamEvent.Delta("Your reply"));
                return Result.Success(new AiResponse { TextMessage = "Your reply" });
            });
        var result = await CreateHandler().Handle(new ProcessUserChatCommand(UserId, "Hello", StreamSink: item =>
        {
            streamed.Add(item);
            return Task.CompletedTask;
        }), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("Your reply");
        streamed.Should().Contain(item => item.Type == "delta");
        _conversationRecorder.Received(1).TryRecord(Arg.Any<ConversationTurnCapture>());
    }

    [Fact]
    public async Task ConversationRecorder_FlagOff_DoesNotRecord()
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = "Hello" });
        var result = await CreateHandler().Handle(new ProcessUserChatCommand(UserId, "Hello"), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        _conversationRecorder.DidNotReceiveWithAnyArgs().TryRecord(default!);
    }

    [Fact]
    public async Task ConversationRecorder_CrisisWithFlagOff_DoesNotRecord()
    {
        SetupUserAndPayGate();

        var result = await CreateHandler().Handle(new(UserId, "I want to hurt myself"), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Contain(CrisisSupportGuard.EnglishResource);
        _conversationRecorder.DidNotReceiveWithAnyArgs().TryRecord(default!);
        await _moderation.DidNotReceiveWithAnyArgs().CheckTextAsync(default!, default);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ConversationRecorder_ModeratedBlockRefusal_QueuesDiagnosticWithoutRunningTurn(bool flagged, bool unavailable)
    {
        SetupUserAndPayGate();
        EnableRecording();
        _unitOfWork.PassThroughTransactions<Result<PreparedAstraBlockAnswer>>();
        var block = IssuedAstraBlock.Create(UserId, new("choice", "When?",
            [new("a", "Morning"), new("b", "Evening")], []));
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(flagged, unavailable, []));

        var result = await CreateHandler().Handle(new(UserId, "My answer", BlockAnswer: new(block.Id, ["b"])), default);

        result.IsFailure.Should().BeTrue();
        _conversationRecorder.Received(1).TryRecord(Arg.Is<ConversationTurnCapture>(turn =>
            turn.UserId == UserId && turn.UserContent == "My answer" && turn.AssistantContent == null
            && !turn.UsedModel && turn.ToolCallsJson == "[]"));
        await _moderation.Received(1).CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _blockStore.DidNotReceiveWithAnyArgs().MarkAnsweredAsync(default, default, default, default);
        await _aiIntentService.DidNotReceiveWithAnyArgs().SendWithToolsAsync(default!, default, default);
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Fact]
    public async Task ConversationRecorder_TwoSuccessfulTools_PreservesOrderAndArguments()
    {
        SetupUserAndPayGate();
        EnableRecording();
        var first = ReadTool("read_one", new ToolResult(true));
        var second = ReadTool("read_two", new ToolResult(true));
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new AiToolCall("read_one", "one", ParseArguments("{\"value\":1}")),
                new AiToolCall("read_two", "two", ParseArguments("{\"value\":2}"))],
            ConversationContext = TestConversationContext
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Done" }));
        ConversationTurnCapture? recorded = null;
        _conversationRecorder.TryRecord(Arg.Do<ConversationTurnCapture>(turn => recorded = turn)).Returns(true);
        var result = await CreateHandler(first, second).Handle(new ProcessUserChatCommand(UserId, "Read both"), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        recorded.Should().NotBeNull();
        var tools = JsonSerializer.Deserialize<ConversationToolCall[]>(recorded!.ToolCallsJson)!;
        tools.Select(tool => tool.CallId).Should().Equal("one", "two");
        tools.Select(tool => tool.Success).Should().Equal(true, true);
        tools.Select(tool => tool.Arguments.GetProperty("value").GetInt32()).Should().Equal(1, 2);
    }

    private void EnableRecording() => _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
        .Returns(new[] { FeatureFlagKeys.AstraConversationRecording });
}
