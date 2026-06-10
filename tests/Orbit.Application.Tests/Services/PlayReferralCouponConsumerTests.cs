using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Services;

public class PlayReferralCouponConsumerTests
{
    private readonly IReferralRewardService _rewardService = Substitute.For<IReferralRewardService>();

    private PlayReferralCouponConsumer CreateConsumer(string referralOfferId = "referral10") =>
        new(
            Options.Create(new GooglePlaySettings { ReferralOfferId = referralOfferId }),
            _rewardService,
            Substitute.For<ILogger<PlayReferralCouponConsumer>>());

    private static User UserWithCoupon()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetReferralCoupon("coupon_abc");
        return user;
    }

    private static PlaySubscriptionState StateWithOffer(string? offerId) =>
        new(true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly, true, "orbit_pro", null, null, offerId);

    [Fact]
    public async Task NewPurchaseWithReferralOfferAndCoupon_ClearsAndCancelsCoupon()
    {
        var user = UserWithCoupon();

        await CreateConsumer().ConsumeOnNewPurchaseAsync(user, StateWithOffer("referral10"), "tok_new", CancellationToken.None);

        user.ReferralCouponId.Should().BeNull();
        await _rewardService.Received(1).CancelCouponAsync("coupon_abc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OfferIdMatchIsCaseInsensitive()
    {
        var user = UserWithCoupon();

        await CreateConsumer().ConsumeOnNewPurchaseAsync(user, StateWithOffer("REFERRAL10"), "tok_new", CancellationToken.None);

        user.ReferralCouponId.Should().BeNull();
    }

    [Fact]
    public async Task PurchaseWithoutReferralOffer_RetainsCoupon()
    {
        var user = UserWithCoupon();

        await CreateConsumer().ConsumeOnNewPurchaseAsync(user, StateWithOffer(null), "tok_new", CancellationToken.None);

        user.ReferralCouponId.Should().Be("coupon_abc");
        await _rewardService.DidNotReceive().CancelCouponAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AlreadyLinkedToken_DoesNotConsume()
    {
        var user = UserWithCoupon();
        user.LinkPlayPurchaseToken("tok_existing");

        await CreateConsumer().ConsumeOnNewPurchaseAsync(user, StateWithOffer("referral10"), "tok_existing", CancellationToken.None);

        user.ReferralCouponId.Should().Be("coupon_abc");
        await _rewardService.DidNotReceive().CancelCouponAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReferralOfferWithoutCoupon_DoesNothing()
    {
        var user = User.Create("Thomas", "test@example.com").Value;

        await CreateConsumer().ConsumeOnNewPurchaseAsync(user, StateWithOffer("referral10"), "tok_new", CancellationToken.None);

        user.ReferralCouponId.Should().BeNull();
        await _rewardService.DidNotReceive().CancelCouponAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelCouponFailure_CouponStillClearedAndNoThrow()
    {
        var user = UserWithCoupon();
        _rewardService.CancelCouponAsync("coupon_abc", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("stripe down"));

        var act = () => CreateConsumer().ConsumeOnNewPurchaseAsync(user, StateWithOffer("referral10"), "tok_new", CancellationToken.None);

        await act.Should().NotThrowAsync();
        user.ReferralCouponId.Should().BeNull();
    }

    [Fact]
    public async Task ReferralOfferIdNotConfigured_DoesNothing()
    {
        var user = UserWithCoupon();

        await CreateConsumer(referralOfferId: "").ConsumeOnNewPurchaseAsync(user, StateWithOffer("referral10"), "tok_new", CancellationToken.None);

        user.ReferralCouponId.Should().Be("coupon_abc");
        await _rewardService.DidNotReceive().CancelCouponAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
