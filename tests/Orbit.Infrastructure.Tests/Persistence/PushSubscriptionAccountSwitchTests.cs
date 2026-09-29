using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Notifications.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

/// <summary>
/// Drives <see cref="SubscribePushCommandHandler"/> and <see cref="UnsubscribePushCommandHandler"/>
/// through the real repository, unit of work and unique endpoint index, one context per request, so
/// the per-account row counts below are the ones the subscriptions list reads.
/// </summary>
public sealed class PushSubscriptionAccountSwitchTests : IDisposable
{
    private const string WebEndpoint = "https://push.example.com/browser-1";
    private const string WebP256dh = "browser-1-p256dh-public-key";
    private const string WebAuth = "browser-1-auth-secret";
    private const string FcmToken = "fcm-token-device-1";

    private readonly SqliteOrbitDbContextFactory _factory = new();
    private readonly Guid _firstAccount;
    private readonly Guid _secondAccount;

    public PushSubscriptionAccountSwitchTests()
    {
        _firstAccount = SeedUser("first@example.com");
        _secondAccount = SeedUser("second@example.com");
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task SameBrowserSignsInToAnotherAccount_OldAccountNoLongerListsIt()
    {
        (await Subscribe(_firstAccount, WebEndpoint, WebP256dh, WebAuth)).IsSuccess.Should().BeTrue();

        var result = await Subscribe(_secondAccount, WebEndpoint, WebP256dh, WebAuth);

        result.IsSuccess.Should().BeTrue();
        CountFor(_firstAccount).Should().Be(0);
        CountFor(_secondAccount).Should().Be(1);
    }

    [Fact]
    public async Task SameAndroidDeviceSignsInToAnotherAccount_OldAccountNoLongerListsIt()
    {
        (await Subscribe(_firstAccount, FcmToken, PushSubscription.FcmSentinel, PushSubscription.FcmSentinel)).IsSuccess.Should().BeTrue();

        var result = await Subscribe(_secondAccount, FcmToken, PushSubscription.FcmSentinel, PushSubscription.FcmSentinel);

        result.IsSuccess.Should().BeTrue();
        CountFor(_firstAccount).Should().Be(0);
        CountFor(_secondAccount).Should().Be(1);
    }

    [Fact]
    public async Task RepeatedAccountSwitchesOnOneDevice_NeverGrowEitherAccountTowardTheCap()
    {
        for (var round = 0; round < AppConstants.MaxPushSubscriptionsPerUser * 2; round++)
        {
            var account = round % 2 == 0 ? _firstAccount : _secondAccount;

            (await Subscribe(account, WebEndpoint, WebP256dh, WebAuth)).IsSuccess.Should().BeTrue();
            (await Subscribe(account, FcmToken, PushSubscription.FcmSentinel, PushSubscription.FcmSentinel)).IsSuccess.Should().BeTrue();

            CountFor(account).Should().Be(2);
            CountFor(round % 2 == 0 ? _secondAccount : _firstAccount).Should().Be(0);
        }
    }

    [Fact]
    public async Task NewAccountTurnsPushOffOnTheBrowser_OldAccountNoLongerListsIt()
    {
        (await Subscribe(_firstAccount, WebEndpoint, WebP256dh, WebAuth)).IsSuccess.Should().BeTrue();

        var result = await Unsubscribe(_secondAccount, WebEndpoint, WebP256dh, WebAuth);

        result.IsSuccess.Should().BeTrue();
        CountFor(_firstAccount).Should().Be(0);
    }

    [Fact]
    public async Task NewAccountSignsOutOfTheAndroidDevice_OldAccountNoLongerListsIt()
    {
        (await Subscribe(_firstAccount, FcmToken, PushSubscription.FcmSentinel, PushSubscription.FcmSentinel)).IsSuccess.Should().BeTrue();

        var result = await Unsubscribe(_secondAccount, FcmToken, PushSubscription.FcmSentinel, PushSubscription.FcmSentinel);

        result.IsSuccess.Should().BeTrue();
        CountFor(_firstAccount).Should().Be(0);
    }

    [Fact]
    public async Task AnotherAccountWithTheEndpointButNotItsKeys_CannotTakeOrRemoveTheDevice()
    {
        (await Subscribe(_firstAccount, WebEndpoint, WebP256dh, WebAuth)).IsSuccess.Should().BeTrue();

        var takeover = await Subscribe(_secondAccount, WebEndpoint, WebP256dh, "guessed-auth-secret");
        var guessedRemoval = await Unsubscribe(_secondAccount, WebEndpoint, WebP256dh, "guessed-auth-secret");
        var endpointOnlyRemoval = await Unsubscribe(_secondAccount, WebEndpoint, null, null);

        takeover.IsFailure.Should().BeTrue();
        takeover.ErrorCode.Should().Be(ErrorCodes.PushEndpointOwnedByOtherUser);
        guessedRemoval.IsSuccess.Should().BeTrue();
        endpointOnlyRemoval.IsSuccess.Should().BeTrue();
        CountFor(_firstAccount).Should().Be(1);
        CountFor(_secondAccount).Should().Be(0);
    }

    private async Task<Result> Subscribe(Guid userId, string endpoint, string p256dh, string auth)
    {
        await using var context = _factory.CreateContext();
        var handler = new SubscribePushCommandHandler(
            new GenericRepository<PushSubscription>(context),
            new UnitOfWork(context, new DatabaseConnectionSettings()));

        return await handler.Handle(new SubscribePushCommand(userId, endpoint, p256dh, auth), CancellationToken.None);
    }

    private async Task<Result> Unsubscribe(Guid userId, string endpoint, string? p256dh, string? auth)
    {
        await using var context = _factory.CreateContext();
        var handler = new UnsubscribePushCommandHandler(
            new GenericRepository<PushSubscription>(context),
            new UnitOfWork(context, new DatabaseConnectionSettings()));

        return await handler.Handle(new UnsubscribePushCommand(userId, endpoint, p256dh, auth), CancellationToken.None);
    }

    private int CountFor(Guid userId)
    {
        using var context = _factory.CreateContext();
        return context.PushSubscriptions.AsNoTracking().Count(subscription => subscription.UserId == userId);
    }

    private Guid SeedUser(string email)
    {
        var user = User.Create("Tester", email).Value;
        _factory.Context.Users.Add(user);
        _factory.Context.SaveChanges();
        return user.Id;
    }
}
