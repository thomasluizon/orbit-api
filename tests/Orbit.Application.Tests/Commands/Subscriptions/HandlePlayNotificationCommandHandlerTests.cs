using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Subscriptions;

public class HandlePlayNotificationCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPlayBillingService _playBilling = Substitute.For<IPlayBillingService>();
    private readonly HandlePlayNotificationCommandHandler _handler;

    public HandlePlayNotificationCommandHandlerTests()
    {
        _handler = new HandlePlayNotificationCommandHandler(
            _userRepo, _unitOfWork, _playBilling,
            Substitute.For<ILogger<HandlePlayNotificationCommandHandler>>());
    }

    private void StubUser(User? user) =>
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

    private void StubVerify(PlaySubscriptionState? state) =>
        _playBilling.VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(state);

    private static string BuildPushBody(int notificationType, string purchaseToken, string subscriptionId)
    {
        var developerNotification = JsonSerializer.Serialize(new
        {
            version = "1.0",
            packageName = "org.useorbit.app",
            eventTimeMillis = "1700000000000",
            subscriptionNotification = new
            {
                version = "1.0",
                notificationType,
                purchaseToken,
                subscriptionId,
            },
        });
        return WrapEnvelope(developerNotification);
    }

    private static string WrapEnvelope(string developerNotificationJson)
    {
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(developerNotificationJson));
        return JsonSerializer.Serialize(new
        {
            message = new { data, messageId = "1" },
            subscription = "projects/x/subscriptions/y",
        });
    }

    [Fact]
    public async Task Handle_MalformedBody_ReturnsSuccessWithoutSaving()
    {
        var result = await _handler.Handle(new HandlePlayNotificationCommand("not-json"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoSubscriptionNotification_ReturnsSuccessWithoutVerifying()
    {
        var body = WrapEnvelope("""{"version":"1.0","testNotification":{"version":"1.0"}}""");

        var result = await _handler.Handle(new HandlePlayNotificationCommand(body), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _playBilling.DidNotReceive().VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ActiveState_GrantsPro()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        StubUser(user);
        StubVerify(new PlaySubscriptionState(true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly, true, "orbit_pro", null));

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(2, "tok_renew", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.IsPro.Should().BeTrue();
        user.PlayPurchaseToken.Should().Be("tok_renew");
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InactiveState_CancelsSubscription()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetPlaySubscription("tok_old", DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly);
        StubUser(user);
        StubVerify(new PlaySubscriptionState(false, DateTime.UtcNow.AddDays(-1), null, false, "orbit_pro", null));

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(13, "tok_old", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.Plan.Should().Be(UserPlan.Free);
        user.PlayPurchaseToken.Should().BeNull();
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsSuccessWithoutSaving()
    {
        StubUser(null);
        StubVerify(new PlaySubscriptionState(true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly, true, "orbit_pro", null));

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(4, "tok_unknown", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_LinkedPurchaseToken_FindsUserByLinkedTokenAndRepoints()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetPlaySubscription("tok_old", DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly);
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null, user);
        StubVerify(new PlaySubscriptionState(true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Yearly, true, "orbit_pro", "tok_old"));

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(7, "tok_new", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.PlayPurchaseToken.Should().Be("tok_new");
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_VerifyThrows_ReturnsFailureSoPubSubRetries()
    {
        StubUser(User.Create("Thomas", "test@example.com").Value);
        _playBilling.VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new BillingProviderException("boom"));

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(2, "tok_renew", "orbit_pro")), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NullState_ReturnsSuccessWithoutSaving()
    {
        StubVerify(null);

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(2, "tok_renew", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
