using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Notifications.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", "subscribe_push")]
    [InlineData("en", "unsubscribe_push")]
    [InlineData("pt-BR", "subscribe_push")]
    [InlineData("pt-BR", "unsubscribe_push")]
    public async Task PushPreviewReplay_ReadOnlyApprovalSurvivesChatComposition(string locale, string action)
    {
        SetupUserAndPayGate();
        EnableComposition();
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var tool = new UpdateNotificationsTool(_mediator);
        var handler = CreateHandler(tool);
        var outcomes = new List<AgentExecuteOperationResponse>();
        UseCapacityReplayExecutor(tool, outcomes);
        SetupAiResponse(ToolResponse(tool.Name, "push", JsonSerializer.Serialize(new
        {
            action,
            endpoint = "https://fcm.googleapis.com/fcm/send/device",
            p256dh = "device-key",
            auth = "device-auth",
            release_other_account = true,
            orbit_write_complete = true
        })));

        var result = await handler.Handle(new(UserId, "Prepare the requested preview.",
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, Locale: locale)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        outcomes.Should().ContainSingle().Which.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        var preview = result.Value.PendingOperations.Should().ContainSingle().Which;
        preview.Items.Should().ContainSingle().Which.Fields.Should().HaveCount(5)
            .And.OnlyContain(field => !field.IsEditable);
        preview.ConfirmationRequirement.Should().Be(AgentConfirmationRequirement.FreshConfirmation);
        preview.PreviewFingerprint.Should().NotBeNullOrWhiteSpace();
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Parts.Should().BeEmpty();
        result.Value.FollowUps.Should().BeNullOrEmpty();
        await _mediator.DidNotReceive().Send(Arg.Any<SubscribePushCommand>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<UnsubscribePushCommand>(), Arg.Any<CancellationToken>());
    }
}
