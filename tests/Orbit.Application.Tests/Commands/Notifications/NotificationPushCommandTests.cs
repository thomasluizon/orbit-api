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
            "Orbit Test",
            "Push notifications are working!",
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
}

public class UnsubscribePushCommandHandlerTests
{
    private readonly IGenericRepository<PushSubscription> _pushSubRepo = Substitute.For<IGenericRepository<PushSubscription>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly UnsubscribePushCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public UnsubscribePushCommandHandlerTests()
    {
        _handler = new UnsubscribePushCommandHandler(_pushSubRepo, _unitOfWork);
    }

    [Fact]
    public async Task Handle_SubscriptionFound_RemovesAndSaves()
    {
        var subscription = PushSubscription.Create(UserId, "https://push.example.com/endpoint", "p256dh", "auth").Value;

        _pushSubRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<PushSubscription, bool>>>(),
            Arg.Any<Func<IQueryable<PushSubscription>, IQueryable<PushSubscription>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(subscription);

        var command = new UnsubscribePushCommand(UserId, "https://push.example.com/endpoint");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _pushSubRepo.Received(1).Remove(subscription);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SubscriptionNotFound_StillReturnsSuccess()
    {
        _pushSubRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<PushSubscription, bool>>>(),
            Arg.Any<Func<IQueryable<PushSubscription>, IQueryable<PushSubscription>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((PushSubscription?)null);

        var command = new UnsubscribePushCommand(UserId, "https://push.example.com/endpoint");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _pushSubRepo.DidNotReceive().Remove(Arg.Any<PushSubscription>());
    }
}
