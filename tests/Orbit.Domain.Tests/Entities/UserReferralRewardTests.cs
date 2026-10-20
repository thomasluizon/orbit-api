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
    public void GrantBonusProDays_LegacySubscriptionRenewalSupersedesBonusMarker()
    {
        var user = User.Create("Alex", "alex@example.com", _clock).Value;
        user.SetStripeSubscription("sub_paid", _clock.GetUtcNow().UtcDateTime.AddDays(-1));
        user.GrantBonusProDays(7);
        typeof(User).GetProperty("PlanExpiresAt")!.SetValue(user, _clock.GetUtcNow().UtcDateTime.AddDays(30));
        user.HasActivePaidSubscription.Should().BeTrue();
        var grant = () => user.GrantBonusProDays(7);
        grant.Should().Throw<InvalidOperationException>();
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
