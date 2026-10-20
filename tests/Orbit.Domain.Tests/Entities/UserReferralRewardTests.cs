using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class UserReferralRewardTests
{
    private readonly Clock _clock = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void GrantBonusProDays_ExpiresWithoutAnotherWrite()
    {
        var user = User.Create("Alex", "alex@example.com", _clock).Value;
        user.GrantBonusProDays(7);
        var expiry = user.PlanExpiresAt;
        user.Plan.Should().Be(UserPlan.Pro);
        user.HasProAccess.Should().BeTrue();
        _clock.Advance(TimeSpan.FromDays(7));
        user.HasProAccess.Should().BeFalse();
        user.PlanExpiresAt.Should().Be(expiry);
        user.SubscriptionSource.Should().BeNull();
        user.StripeSubscriptionId.Should().BeNull();
        user.PlayPurchaseToken.Should().BeNull();
    }

    [Fact]
    public void GrantBonusProDays_StacksFromUnexpiredBonus()
    {
        var user = User.Create("Alex", "alex@example.com", _clock).Value;
        user.GrantBonusProDays(7);
        _clock.Advance(TimeSpan.FromDays(1));
        user.GrantBonusProDays(7);
        user.PlanExpiresAt.Should().Be(new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc));
        user.HasActivePaidSubscription.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GrantBonusProDays_RejectsActivePaidPeriod(bool play)
    {
        var user = User.Create("Alex", "alex@example.com", _clock).Value;
        var expiry = _clock.GetUtcNow().UtcDateTime.AddDays(30);
        if (play)
            user.SetPlaySubscription("play-token", expiry);
        else
            user.SetStripeSubscription("sub_paid", expiry);
        var grant = () => user.GrantBonusProDays(7);
        grant.Should().Throw<InvalidOperationException>();
        user.PlanExpiresAt.Should().Be(expiry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GrantBonusProDays_ExpiredSubscriptionStartsFromNowAndKeepsProvider(bool play)
    {
        var user = User.Create("Alex", "alex@example.com", _clock).Value;
        var expired = _clock.GetUtcNow().UtcDateTime.AddDays(-1);
        if (play)
            user.SetPlaySubscription("play-token", expired);
        else
            user.SetStripeSubscription("sub_expired", expired);
        user.GrantBonusProDays(7);
        user.PlanExpiresAt.Should().Be(_clock.GetUtcNow().UtcDateTime.AddDays(7));
        user.HasActivePaidSubscription.Should().BeFalse();
        user.GrantBonusProDays(7);
        user.PlanExpiresAt.Should().Be(_clock.GetUtcNow().UtcDateTime.AddDays(14));
        user.SubscriptionSource.Should().Be(play ? SubscriptionSource.GooglePlay : SubscriptionSource.Stripe);
        if (play)
            user.PlayPurchaseToken.Should().Be("play-token");
        else
            user.StripeSubscriptionId.Should().Be("sub_expired");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GrantBonusProDays_RejectsInvalidDays(int days)
    {
        var user = User.Create("Alex", "alex@example.com", _clock).Value;
        var grant = () => user.GrantBonusProDays(days);
        grant.Should().Throw<ArgumentOutOfRangeException>();
        user.Plan.Should().Be(UserPlan.Free);
        user.PlanExpiresAt.Should().BeNull();
    }

    [Fact]
    public void GrantBonusProDays_SubscriptionRenewalPreservesBonusAndRejectsPaidGrant()
    {
        var user = User.Create("Alex", "alex@example.com", _clock).Value;
        user.SetStripeSubscription("sub_paid", _clock.GetUtcNow().UtcDateTime.AddDays(-1));
        user.GrantBonusProDays(7);
        var bonusExpiry = user.BonusProExpiresAtUtc;
        user.SetStripeSubscription("sub_paid", _clock.GetUtcNow().UtcDateTime.AddDays(30));
        user.BonusProExpiresAtUtc.Should().Be(bonusExpiry);
        user.HasActivePaidSubscription.Should().BeTrue();
        var grant = () => user.GrantBonusProDays(7);
        grant.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GrantBonusProDays_TerminalProviderCancellationPreservesAccessUntilExpiry(bool play)
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var user = User.Create("Alex", "alex@example.com", clock).Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-30));
        var expired = clock.GetUtcNow().UtcDateTime.AddDays(-1);
        if (play)
            user.SetPlaySubscription("play-token", expired);
        else
            user.SetStripeSubscription("sub_expired", expired);
        user.GrantBonusProDays(7);
        var bonusExpiry = user.BonusProExpiresAtUtc;

        if (play)
            user.CancelPlaySubscription(SubscriptionLapseReason.Expired);
        else
            user.CancelStripeSubscription(SubscriptionLapseReason.Canceled);

        user.HasProAccess.Should().BeTrue();
        user.IsTrialActive.Should().BeFalse();
        user.Plan.Should().Be(UserPlan.Pro);
        user.PlanExpiresAt.Should().Be(bonusExpiry);
        user.BonusProExpiresAtUtc.Should().Be(bonusExpiry);
        user.HasActivePaidSubscription.Should().BeFalse();
        user.SubscriptionSource.Should().BeNull();
        user.StripeSubscriptionId.Should().BeNull();
        user.PlayPurchaseToken.Should().BeNull();
        clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromTicks(1));
        user.HasProAccess.Should().BeTrue();
        clock.Advance(TimeSpan.FromTicks(1));
        user.HasProAccess.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BonusAccess_RepeatedProviderCleanupDoesNotResurrectExpiredBonus(bool play)
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var user = User.Create("Alex", "alex@example.com", clock).Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-30));
        if (play)
            user.SetPlaySubscription("play-token", clock.GetUtcNow().UtcDateTime.AddDays(-1));
        else
            user.SetStripeSubscription("sub_expired", clock.GetUtcNow().UtcDateTime.AddDays(-1));
        user.GrantBonusProDays(7);
        var bonusExpiry = user.BonusProExpiresAtUtc;
        void Cancel()
        {
            if (play)
                user.CancelPlaySubscription(SubscriptionLapseReason.PaymentFailed);
            else
                user.CancelStripeSubscription(SubscriptionLapseReason.PaymentFailed);
        }

        Cancel();
        user.HasProAccess.Should().BeTrue();
        clock.Advance(TimeSpan.FromDays(7));
        Cancel();
        user.CancelDeactivation();
        user.StartTrial(clock.GetUtcNow().UtcDateTime.AddDays(-30));

        user.HasProAccess.Should().BeFalse();
        user.BonusProExpiresAtUtc.Should().Be(bonusExpiry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BonusAccess_NewPurchasePreservesIndependentExpiry(bool play)
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var user = User.Create("Alex", "alex@example.com", clock).Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-30));
        user.GrantBonusProDays(7);
        var bonusExpiry = user.BonusProExpiresAtUtc;
        var paidExpiry = clock.GetUtcNow().UtcDateTime.AddDays(2);
        if (play)
            user.SetPlaySubscription("play-token", paidExpiry);
        else
            user.SetStripeSubscription("sub_new", paidExpiry);

        user.BonusProExpiresAtUtc.Should().Be(bonusExpiry);
        user.PlanExpiresAt.Should().Be(bonusExpiry);
        user.HasActivePaidSubscription.Should().BeTrue();
        clock.Advance(TimeSpan.FromDays(2));
        user.HasActivePaidSubscription.Should().BeFalse();
        user.HasProAccess.Should().BeTrue();
        clock.Advance(TimeSpan.FromDays(5));
        user.HasProAccess.Should().BeFalse();
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
