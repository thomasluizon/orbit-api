using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("free", false)]
    [InlineData("trial", false)]
    [InlineData("pro", false)]
    [InlineData("free", true)]
    public async Task Handle_MessageAnalytics_UsesQuotaEntitlementAndModelRoundCounts(string plan, bool fails)
    {
        var user = User.Create("private-person", "private@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(plan == "trial" ? 1 : -1));
        if (plan == "pro")
            user.GrantLifetimePro();
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
        telemetry.Plan.Should().Be(plan);
        _productAnalytics.Received(1).CaptureUserEvent(UserId, "astra_message_sent", plan,
            Arg.Is<IReadOnlyDictionary<string, object>>(properties => properties.Count == 3
                && (string)properties["plan"] == plan && (int)properties["round_count"] == 2
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

    [Fact]
    public async Task Handle_CrisisMessage_EmitsOneMessageEventWithZeroModelCalls()
    {
        SetupUserAndPayGate();

        await CreateHandler().Handle(new ProcessUserChatCommand(UserId, "I want to kill myself"), CancellationToken.None);

        _productAnalytics.Received(1).CaptureUserEvent(UserId, "astra_message_sent", Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<string, object>>(properties => (int)properties["round_count"] == 0
                && (int)properties["tool_call_count"] == 0));
    }
}
