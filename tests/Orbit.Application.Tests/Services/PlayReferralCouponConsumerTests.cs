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
    public void NewPurchaseWithReferralOfferAndCoupon_ClearsCouponAndReturnsCouponId()
    {
        var user = UserWithCoupon();

        var couponId = CreateConsumer().ConsumeOnNewPurchase(user, StateWithOffer("referral10"), "tok_new");

        couponId.Should().Be("coupon_abc");
        user.ReferralCouponId.Should().BeNull();
    }

    [Fact]
    public void OfferIdMatchIsCaseInsensitive()
    {
        var user = UserWithCoupon();

        var couponId = CreateConsumer().ConsumeOnNewPurchase(user, StateWithOffer("REFERRAL10"), "tok_new");

        couponId.Should().Be("coupon_abc");
        user.ReferralCouponId.Should().BeNull();
    }

    [Fact]
    public void PurchaseWithoutReferralOffer_RetainsCouponAndReturnsNull()
    {
        var user = UserWithCoupon();

        var couponId = CreateConsumer().ConsumeOnNewPurchase(user, StateWithOffer(null), "tok_new");

        couponId.Should().BeNull();
        user.ReferralCouponId.Should().Be("coupon_abc");
    }

    [Fact]
    public void AlreadyLinkedToken_DoesNotConsume()
    {
        var user = UserWithCoupon();
        user.LinkPlayPurchaseToken("tok_existing");

        var couponId = CreateConsumer().ConsumeOnNewPurchase(user, StateWithOffer("referral10"), "tok_existing");

        couponId.Should().BeNull();
        user.ReferralCouponId.Should().Be("coupon_abc");
    }

    [Fact]
    public void ReferralOfferWithoutCoupon_ReturnsNull()
    {
        var user = User.Create("Thomas", "test@example.com").Value;

        var couponId = CreateConsumer().ConsumeOnNewPurchase(user, StateWithOffer("referral10"), "tok_new");

        couponId.Should().BeNull();
        user.ReferralCouponId.Should().BeNull();
    }

    [Fact]
    public void ReferralOfferIdNotConfigured_ReturnsNull()
    {
        var user = UserWithCoupon();

        var couponId = CreateConsumer(referralOfferId: "").ConsumeOnNewPurchase(user, StateWithOffer("referral10"), "tok_new");

        couponId.Should().BeNull();
        user.ReferralCouponId.Should().Be("coupon_abc");
    }

    [Fact]
    public async Task CancelConsumedCouponAsync_CancelsAtBillingProvider()
    {
        await CreateConsumer().CancelConsumedCouponAsync(Guid.NewGuid(), "coupon_abc", CancellationToken.None);

        await _rewardService.Received(1).CancelCouponAsync("coupon_abc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelConsumedCouponAsync_BillingProviderFailure_DoesNotThrow()
    {
        _rewardService.CancelCouponAsync("coupon_abc", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("stripe down"));

        var act = () => CreateConsumer().CancelConsumedCouponAsync(Guid.NewGuid(), "coupon_abc", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
