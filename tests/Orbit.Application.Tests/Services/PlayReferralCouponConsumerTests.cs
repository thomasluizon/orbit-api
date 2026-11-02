using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Services;

public class PlayReferralCouponConsumerTests
{
    private PlayReferralCouponConsumer CreateConsumer(string referralOfferId = "referral10") =>
        new(
            Options.Create(new GooglePlaySettings { ReferralOfferId = referralOfferId }),
            Substitute.For<ILogger<PlayReferralCouponConsumer>>());

    private static User UserWithCoupon()
    {
        var user = User.Create("Alex", "test@example.com").Value;
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
        var user = User.Create("Alex", "test@example.com").Value;

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
}
