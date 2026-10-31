using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Notifications.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public sealed partial class AstraApprovalPathTests
{
    [Theory]
    [InlineData("subscribe_push", false)]
    [InlineData("unsubscribe_push", false)]
    [InlineData("unsubscribe_push", true)]
    public async Task PushApproval_ExecutesOriginalStoredDeviceArguments(string action, bool releaseOtherAccount)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        mediator.Send(Arg.Any<SubscribePushCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        mediator.Send(Arg.Any<UnsubscribePushCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var tool = new UpdateNotificationsTool(mediator);
        var (executor, store, revisions) = Build(tool);
        const string endpoint = "https://fcm.googleapis.com/fcm/send/device";
        const string p256dh = "device-key";
        const string auth = "device-auth";
        var arguments = JsonSerializer.SerializeToElement(new
        {
            action,
            endpoint,
            p256dh,
            auth,
            release_other_account = releaseOtherAccount
        });

        var held = await executor.ExecuteAsync(new(_userId, tool.Name, arguments,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, IncludeChangePreview: true));

        held.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        held.PendingOperation.Should().NotBeNull();
        var preview = held.PendingOperation!;
        preview.Items.Should().ContainSingle().Which.Fields.Should().OnlyContain(field => !field.IsEditable);
        preview.ActionKey.Should().Be(action == "subscribe_push" ? "subscribePush" : "unsubscribePush");
        await mediator.DidNotReceive().Send(Arg.Any<SubscribePushCommand>(), Arg.Any<CancellationToken>());
        await mediator.DidNotReceive().Send(Arg.Any<UnsubscribePushCommand>(), Arg.Any<CancellationToken>());
        _db.ChangeTracker.Clear();
        var pending = store.GetExecution(_userId, preview.Id)!;
        JsonElement.DeepEquals(pending.Arguments, arguments).Should().BeTrue();
        (await revisions.CanApproveAsync(_userId, pending, default)).Should().BeTrue();

        var confirmation = store.Confirm(_userId, preview.Id)!;
        var approved = await executor.ExecuteAsync(new(_userId, tool.Name, pending.Arguments,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        if (action == "subscribe_push")
            await mediator.Received(1).Send(Arg.Is<SubscribePushCommand>(command =>
                command.UserId == _userId && command.Endpoint == endpoint && command.P256dh == p256dh
                && command.Auth == auth), Arg.Any<CancellationToken>());
        else
            await mediator.Received(1).Send(Arg.Is<UnsubscribePushCommand>(command =>
                command.UserId == _userId && command.Endpoint == endpoint && command.P256dh == p256dh
                && command.Auth == auth && command.ReleaseOtherAccount == releaseOtherAccount), Arg.Any<CancellationToken>());
    }
}
