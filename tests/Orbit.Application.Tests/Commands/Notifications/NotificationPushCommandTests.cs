using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Notifications.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Notifications;

public class TestPushNotificationCommandHandlerTests
{
    private readonly IGenericRepository<PushSubscription> _pushSubRepo = Substitute.For<IGenericRepository<PushSubscription>>();
    private readonly IPushNotificationService _pushService = Substitute.For<IPushNotificationService>();
    private readonly TestPushNotificationCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public TestPushNotificationCommandHandlerTests()
    {
        _handler = new TestPushNotificationCommandHandler(_pushSubRepo, _pushService);
    }

    [Fact]
    public async Task Handle_HasSubscriptions_SendsTestPush()
    {
        _pushSubRepo.CountAsync(
            Arg.Any<Expression<Func<PushSubscription, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(2);

        var command = new TestPushNotificationCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.SubscriptionCount.Should().Be(2);
        result.Value.Status.Should().Be("sent");
        await _pushService.Received(1).SendToUserAsync(
            UserId,
            "Orbit test",
            "Push notifications are working.",
            "/",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoSubscriptions_ReturnsFailure()
    {
        _pushSubRepo.CountAsync(
            Arg.Any<Expression<Func<PushSubscription, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(0);

        var command = new TestPushNotificationCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("No push subscriptions found for this user.");
    }

    [Fact]
    public async Task Handle_PushServiceThrows_ReturnsSuccessWithFailedStatus()
    {
        _pushSubRepo.CountAsync(
            Arg.Any<Expression<Func<PushSubscription, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(1);

        _pushService.SendToUserAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Push failed"));

        var command = new TestPushNotificationCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("failed");
        result.Value.Error.Should().Contain("Failed to send");
    }

    [Fact]
    public async Task Handle_Cancelled_PropagatesCancellation()
    {
        _pushSubRepo.CountAsync(
            Arg.Any<Expression<Func<PushSubscription, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(1);

        _pushService.SendToUserAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var command = new TestPushNotificationCommand(UserId);

        var act = () => _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

public class UnsubscribePushCommandHandlerTests
{
    private readonly IGenericRepository<PushSubscription> _pushSubRepo = Substitute.For<IGenericRepository<PushSubscription>>();
    private readonly UnsubscribePushCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public UnsubscribePushCommandHandlerTests()
    {
        _handler = new UnsubscribePushCommandHandler(_pushSubRepo);
    }

    [Fact]
    public async Task Handle_SubscriptionFound_DeletesOnlyTheReadRowAndOwner()
    {
        var subscription = PushSubscription.Create(UserId, "https://push.example.com/endpoint", "p256dh", "auth").Value;
        var otherSubscription = PushSubscription.Create(UserId, "https://push.example.com/other", "p256dh", "auth").Value;
        using var cancellation = new CancellationTokenSource();

        _pushSubRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<PushSubscription, bool>>>(),
            Arg.Any<Func<IQueryable<PushSubscription>, IQueryable<PushSubscription>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(subscription);

        var command = new UnsubscribePushCommand(UserId, "https://push.example.com/endpoint", "p256dh", "auth");

        var result = await _handler.Handle(command, cancellation.Token);

        result.IsSuccess.Should().BeTrue();
        await _pushSubRepo.Received(1).DeleteAsync(
            Arg.Is<Expression<Func<PushSubscription, bool>>>(predicate =>
                predicate.Compile()(subscription) && !predicate.Compile()(otherSubscription)),
            cancellation.Token);

        subscription.TransferTo(Guid.NewGuid()).IsSuccess.Should().BeTrue();
        await _pushSubRepo.Received(1).DeleteAsync(
            Arg.Is<Expression<Func<PushSubscription, bool>>>(predicate => !predicate.Compile()(subscription)),
            cancellation.Token);
    }

    [Fact]
    public async Task Handle_ExplicitCrossAccountReleaseWithDeviceKeys_DeletesThePreviousOwnersRow()
    {
        var subscription = PushSubscription.Create(Guid.NewGuid(), "https://push.example.com/endpoint", "p256dh", "auth").Value;
        ArrangeFound(subscription);

        var command = new UnsubscribePushCommand(UserId, "https://push.example.com/endpoint", "p256dh", "auth", ReleaseOtherAccount: true);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _pushSubRepo.Received(1).DeleteAsync(
            Arg.Is<Expression<Func<PushSubscription, bool>>>(predicate => predicate.Compile()(subscription)),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_OrdinaryReleaseWithMatchingCredentials_LeavesAnotherAccountsRowInPlace(bool native)
    {
        var p256dh = native ? PushSubscription.FcmSentinel : "p256dh";
        var auth = native ? PushSubscription.FcmSentinel : "auth";
        var subscription = PushSubscription.Create(Guid.NewGuid(), "endpoint", p256dh, auth).Value;
        ArrangeFound(subscription);

        var result = await _handler.Handle(new UnsubscribePushCommand(UserId, "endpoint", p256dh, auth), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _pushSubRepo.DidNotReceive().DeleteAsync(
            Arg.Any<Expression<Func<PushSubscription, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("p256dh", "other-auth")]
    [InlineData(null, null)]
    public async Task Handle_OtherAccountsRowWithoutItsDeviceKeys_LeavesItInPlace(string? p256dh, string? auth)
    {
        var subscription = PushSubscription.Create(Guid.NewGuid(), "https://push.example.com/endpoint", "p256dh", "auth").Value;
        ArrangeFound(subscription);

        var command = new UnsubscribePushCommand(UserId, "https://push.example.com/endpoint", p256dh, auth, ReleaseOtherAccount: true);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _pushSubRepo.DidNotReceive().DeleteAsync(
            Arg.Any<Expression<Func<PushSubscription, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_OwnRowWithoutDeviceKeys_DeletesTheRow()
    {
        var subscription = PushSubscription.Create(UserId, "https://push.example.com/endpoint", "p256dh", "auth").Value;
        ArrangeFound(subscription);

        var command = new UnsubscribePushCommand(UserId, "https://push.example.com/endpoint", null, null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _pushSubRepo.Received(1).DeleteAsync(
            Arg.Is<Expression<Func<PushSubscription, bool>>>(predicate => predicate.Compile()(subscription)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SubscriptionNotFound_StillReturnsSuccess()
    {
        _pushSubRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<PushSubscription, bool>>>(),
            Arg.Any<Func<IQueryable<PushSubscription>, IQueryable<PushSubscription>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((PushSubscription?)null);

        var command = new UnsubscribePushCommand(UserId, "https://push.example.com/endpoint", "p256dh", "auth");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _pushSubRepo.DidNotReceive().DeleteAsync(
            Arg.Any<Expression<Func<PushSubscription, bool>>>(), Arg.Any<CancellationToken>());
    }

    private void ArrangeFound(PushSubscription subscription)
    {
        _pushSubRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<PushSubscription, bool>>>(),
            Arg.Any<Func<IQueryable<PushSubscription>, IQueryable<PushSubscription>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(subscription);
    }
}
