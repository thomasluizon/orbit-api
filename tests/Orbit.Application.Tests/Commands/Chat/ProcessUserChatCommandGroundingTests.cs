using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Grounding;
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
    [InlineData(true, 12)]
    [InlineData(true, 99)]
    [InlineData(false, 12)]
    [InlineData(false, 99)]
    public async Task Handle_PartsClient_GroundsTextAndPreservesOrderedBlocks(bool enforce, int streak)
    {
        SetupUserAndPayGate();
        _appConfig.GetAsync(AppConfigKeys.AstraGroundingEnforce, false, Arg.Any<CancellationToken>()).Returns(enforce);
        var tool = FakeTool("log_habit");
        tool.IsReadOnly.Returns(false);
        tool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true, Payload: new { current_streak = 12 }));
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new AiToolCall(tool.Name, "call_parts", JsonSerializer.SerializeToElement(new { }))],
            ConversationContext = TestConversationContext
        });
        var message = $"Your streak is {streak} days.";
        const string ending = "How can I help?";
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = $"{message}\n[[orbit:habits:today]]\n{ending}" }));
        var events = new List<ChatStreamEvent>();

        var result = await CreateHandler(tool).Handle(new ProcessUserChatCommand(UserId, "Log my habit and show today",
            ClientContext: new AgentClientContext(SupportsParts: true, PartKinds: ["habitList"], SupportsHabitListCard: true),
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), CancellationToken.None);

        var suppressed = enforce && streak != 12;
        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be(suppressed ? null : $"{message}\n\n{ending}");
        result.Value.Parts.Should().NotBeNull();
        var parts = result.Value.Parts!;
        parts.OfType<ChatTextPart>().Select(part => part.Text)
            .Should().Equal(suppressed ? Array.Empty<string>() : new[] { message, ending });
        parts.OfType<ChatBlockPart>().Should().ContainSingle().Which.Kind.Should().Be("habitList");
        if (!suppressed)
            parts[1].Should().BeOfType<ChatBlockPart>();
        if (enforce)
            events.Where(item => item.Type == "delta").Select(item => item.Text)
                .Should().Equal(suppressed ? Array.Empty<string>() : new[] { result.Value.AiMessage });
        result.Value.Actions.Should().ContainSingle().Which.Status.Should().Be(ActionStatus.Success);
        result.Value.GroundingFacts.Facts.Should().Contain(new GroundingFact("Number", "12", tool.Name, "call_parts"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handle_GroundingMode_ControlsBatchAndStreamText(bool enforce)
    {
        SetupUserAndPayGate();
        _appConfig.GetAsync(AppConfigKeys.AstraGroundingEnforce, false, Arg.Any<CancellationToken>()).Returns(enforce);
        const string invented = "You are on a 12-day streak.";
        var events = new List<ChatStreamEvent>();
        var receivedModelSink = false;
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var sink = call.ArgAt<Func<AiStreamEvent, Task>?>(1);
                if (sink is not null)
                {
                    receivedModelSink = true;
                    await sink(AiStreamEvent.Delta(invented));
                }
                return Result.Success(new AiResponse { TextMessage = invented });
            });

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(UserId, "How am I doing?",
            CorrelationId: "grounding-test", StreamSink: streamEvent =>
            {
                events.Add(streamEvent);
                return Task.CompletedTask;
            }), CancellationToken.None);

        if (enforce)
        {
            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be(ErrorCodes.AiEmptyResponse);
        }
        else
        {
            result.IsSuccess.Should().BeTrue();
            result.Value.AiMessage.Should().Be(invented);
        }
        receivedModelSink.Should().Be(!enforce);
        events.Where(item => item.Type == "delta").Select(item => item.Text)
            .Should().Equal(enforce ? Array.Empty<string>() : new[] { invented });
        var warnings = _logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "Log"
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning).ToArray();
        warnings.Should().HaveCount(enforce ? 2 : 1);
        var log = warnings.Should().ContainSingle(call =>
            ((IEnumerable<KeyValuePair<string, object?>>)call.GetArguments()[2]!).Any(pair => pair.Key == "ViolationKind")).Subject;
        var properties = ((IEnumerable<KeyValuePair<string, object?>>)log.GetArguments()[2]!).ToDictionary();
        properties.Should().Contain("ViolationKind", "Number").And.Contain("Claim", "12")
            .And.Contain("Enforced", enforce).And.Contain("CorrelationId", "grounding-test");
    }

    [Theory]
    [InlineData("Your streak is 12 days.", true)]
    [InlineData("Your streak is 99 days.", false)]
    public async Task Handle_EnforcedToolTurn_PreservesActionsAndExposesFacts(string message, bool grounded)
    {
        SetupUserAndPayGate();
        _appConfig.GetAsync(AppConfigKeys.AstraGroundingEnforce, false, Arg.Any<CancellationToken>()).Returns(true);
        var tool = FakeTool("log_habit");
        tool.IsReadOnly.Returns(false);
        tool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true, EntityId: Guid.NewGuid().ToString(), EntityName: "Meditação", Payload: new { current_streak = 12 }));
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new AiToolCall(tool.Name, "call_grounded", JsonSerializer.SerializeToElement(new { }))],
            ConversationContext = TestConversationContext
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = message }));
        var events = new List<ChatStreamEvent>();

        var result = await CreateHandler(tool).Handle(new ProcessUserChatCommand(UserId, "Log my habit", StreamSink: item =>
        {
            events.Add(item);
            return Task.CompletedTask;
        }), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be(grounded ? message : null);
        result.Value.Actions.Should().ContainSingle().Which.Status.Should().Be(ActionStatus.Success);
        result.Value.GroundingFacts.Facts.Should().Contain(new GroundingFact("Number", "12", tool.Name, "call_grounded"));
        events.Where(item => item.Type == "delta").Select(item => item.Text)
            .Should().Equal(grounded ? new[] { message } : Array.Empty<string>());
        JsonSerializer.Serialize(result.Value).Should().NotContain("GroundingFacts").And.NotContain("ToolCallId");
        await _aiIntentService.Received().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NameFromIndexAndHistory_DoesNotGroundCurrentReply()
    {
        SetupUserAndPayGate();
        var habit = CreateHabit("Meditação diária");
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());
        SetupAiResponse(new AiResponse { TextMessage = "MEDITACAO DIARIA está ativo." });

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(UserId, "What is active?",
            History: [new ChatHistoryMessage("assistant", "Meditação diária está ativo.")]), CancellationToken.None);

        result.Value.GroundingFacts.Facts.Should().BeEmpty();
        _logger.ReceivedCalls().Should().Contain(call => call.GetMethodInfo().Name == "Log"
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning
            && call.GetArguments()[2]!.ToString()!.Contains("EntityName"));
    }

    [Fact]
    public async Task Handle_UngroundedFollowUps_AreSuppressedWhileCleanMessageRemains()
    {
        SetupUserAndPayGate();
        _appConfig.GetAsync(AppConfigKeys.AstraGroundingEnforce, false, Arg.Any<CancellationToken>()).Returns(true);
        SetupAiResponse(new AiResponse
        {
            TextMessage = "How can I help?\n[[orbit:followups]]\nShow my 12-day streak\nWhat is next?"
        });

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(UserId, "Help me plan",
            ClientContext: new AgentClientContext(SupportsFollowUps: true)), CancellationToken.None);

        result.Value.AiMessage.Should().Be("How can I help?");
        result.Value.FollowUps.Should().BeNull();
    }

    [Fact]
    public async Task Handle_CleanNoToolTurn_ProducesNoWarnings()
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = "Hello! How can I help?" });

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(UserId, "Hello"), CancellationToken.None);

        result.Value.GroundingFacts.Facts.Should().BeEmpty();
        _logger.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name == "Log"
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning);
    }
}
