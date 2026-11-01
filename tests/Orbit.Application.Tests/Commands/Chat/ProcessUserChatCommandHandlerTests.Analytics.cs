using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("Free", false)]
    [InlineData("Trial", false)]
    [InlineData("Pro", false)]
    [InlineData("Free", true)]
    [InlineData("Subscribed", false)]
    [InlineData("Expired", false)]
    public async Task Handle_MessageAnalytics_UsesStoredPlanAndModelRoundCounts(string plan, bool fails)
    {
        var user = User.Create("private-person", "private@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(plan == "Trial" ? 1 : -1));
        if (plan == "Pro")
            user.GrantLifetimePro();
        if (plan is "Subscribed" or "Expired")
            user.SetStripeSubscription("sub-test", DateTime.UtcNow.AddDays(plan == "Subscribed" ? 1 : -1));
        SetupUserAndPayGate(user);
        AiTurnTelemetry? telemetry = null;
        _aiIntentService.SendWithToolsAsync(Arg.Do<AiToolRequest>(request =>
            {
                telemetry = request.Telemetry;
                telemetry.RecordRound(2);
                telemetry.RecordRound(0);
            }), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(fails ? Result.Failure<AiResponse>("provider failed")
                : Result.Success(new AiResponse { TextMessage = "a helpful reply" }));

        await CreateHandler().Handle(new ProcessUserChatCommand(UserId, "private message contents", CorrelationId: "correlation-1"), CancellationToken.None);

        telemetry.Should().NotBeNull();
        telemetry!.TraceId.Should().Be("correlation-1");
        var storedPlan = user.Plan.ToString();
        telemetry.Plan.Should().Be(storedPlan);
        _productAnalytics.Received(1).CaptureUserEvent(UserId, "astra_message_sent", storedPlan,
            Arg.Is<IReadOnlyDictionary<string, object>>(properties => properties.Count == 3
                && (string)properties["plan"] == storedPlan && (int)properties["round_count"] == 2
                && (int)properties["tool_call_count"] == 2));
    }

    [Fact]
    public async Task Handle_MessageAnalyticsFailure_DoesNotChangeReply()
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = "reply" });
        _productAnalytics.When(analytics => analytics.CaptureUserEvent(UserId, "astra_message_sent", Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>>())).Do(_ => throw new InvalidOperationException("analytics offline"));

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(UserId, "help me build a routine"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("reply");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_CrisisMessage_EmitsOneMessageEventWithZeroModelCalls(bool recordingEnabled)
    {
        SetupUserAndPayGate();
        if (recordingEnabled)
            EnableRecording();

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(UserId, "I want to kill myself"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _productAnalytics.Received(1).CaptureUserEvent(UserId, "astra_message_sent", null,
            Arg.Is<IReadOnlyDictionary<string, object>>(properties => (int)properties["round_count"] == 0
                && (int)properties["tool_call_count"] == 0));
        if (recordingEnabled)
            _ = _conversationRecorder.Received(1).RecordAsync(Arg.Is<ConversationTurnCapture>(turn => turn.UserId == UserId
                && turn.UserContent == "I want to kill myself" && turn.AssistantContent == result.Value.AiMessage
                && !turn.UsedModel && turn.ToolCallsJson == "[]"));
        else
            _ = _conversationRecorder.DidNotReceiveWithAnyArgs().RecordAsync(default!);
    }
}
