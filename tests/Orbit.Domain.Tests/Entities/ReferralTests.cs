using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class ReferralTests
{
    private static readonly Guid ReferrerId = Guid.NewGuid();
    private static readonly Guid ReferredUserId = Guid.NewGuid();

    [Fact]
    public void Create_ValidData_SetsPendingStatusAndTimestamp()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        var referral = Referral.Create(ReferrerId, ReferredUserId);

        var after = DateTime.UtcNow.AddSeconds(1);

        referral.ReferrerId.Should().Be(ReferrerId);
        referral.ReferredUserId.Should().Be(ReferredUserId);
        referral.Status.Should().Be(ReferralStatus.Pending);
        referral.CreatedAtUtc.Should().BeOnOrAfter(before);
        referral.CreatedAtUtc.Should().BeOnOrBefore(after);
        referral.CompletedAtUtc.Should().BeNull();
        referral.RewardGrantedAtUtc.Should().BeNull();
    }

    [Fact]
    public void MarkCompleted_SetsStatusAndTimestamp()
    {
        var referral = Referral.Create(ReferrerId, ReferredUserId);
        var before = DateTime.UtcNow.AddSeconds(-1);

        referral.MarkCompleted();

        var after = DateTime.UtcNow.AddSeconds(1);

        referral.Status.Should().Be(ReferralStatus.Completed);
        referral.CompletedAtUtc.Should().NotBeNull();
        referral.CompletedAtUtc!.Value.Should().BeOnOrAfter(before);
        referral.CompletedAtUtc!.Value.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void MarkRewarded_SetsStatusAndTimestamp()
    {
        var referral = Referral.Create(ReferrerId, ReferredUserId);
        var before = DateTime.UtcNow.AddSeconds(-1);

        referral.MarkRewarded();

        var after = DateTime.UtcNow.AddSeconds(1);

        referral.Status.Should().Be(ReferralStatus.Rewarded);
        referral.RewardGrantedAtUtc.Should().NotBeNull();
        referral.RewardGrantedAtUtc!.Value.Should().BeOnOrAfter(before);
        referral.RewardGrantedAtUtc!.Value.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void MarkRewarded_RepeatedAttemptPreservesOriginalRewardAndTimestamp()
    {
        var referral = Referral.Create(ReferrerId, ReferredUserId);
        referral.MarkRewarded(ReferralRewardKind.ProDays, ReferralRewardKind.Coupon);
        var timestamp = referral.RewardGrantedAtUtc;
        referral.MarkCompleted();
        referral.MarkRewarded();
        referral.ReferrerReward.Should().Be(ReferralRewardKind.ProDays);
        referral.RewardGrantedAtUtc.Should().Be(timestamp);
        referral.Status.Should().Be(ReferralStatus.Rewarded);
    }

    [Fact]
    public void MarkRewardDelivered_RequiresGrantedRewardForParticipant()
    {
        var referral = Referral.Create(ReferrerId, ReferredUserId);
        var pending = () => referral.MarkRewardDelivered(ReferrerId);
        pending.Should().Throw<InvalidOperationException>();
        referral.MarkRewarded(ReferralRewardKind.ProDays, ReferralRewardKind.None);
        var capped = () => referral.MarkRewardDelivered(ReferredUserId);
        capped.Should().Throw<InvalidOperationException>();
        var stranger = () => referral.MarkRewardDelivered(Guid.NewGuid());
        stranger.Should().Throw<ArgumentException>();
        referral.MarkRewardDelivered(ReferrerId);
        var delivered = referral.ReferrerRewardDeliveredAtUtc;
        referral.MarkRewardDelivered(ReferrerId);
        referral.ReferrerRewardDeliveredAtUtc.Should().Be(delivered);
    }

    [Fact]
    public void Create_InvalidAccounts_RejectsAtDomainBoundary()
    {
        var missing = () => Referral.Create(Guid.Empty, ReferredUserId);
        missing.Should().Throw<ArgumentException>();
        var self = () => Referral.Create(ReferrerId, ReferrerId);
        self.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkRewarded_RejectsMissingOrInvalidRewards()
    {
        var referral = Referral.Create(ReferrerId, ReferredUserId);
        var empty = () => referral.MarkRewarded(ReferralRewardKind.None, ReferralRewardKind.None);
        empty.Should().Throw<ArgumentException>();
        var invalid = () => referral.MarkRewarded((ReferralRewardKind)99, ReferralRewardKind.ProDays);
        invalid.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkCompleted_ThenMarkRewarded_BothTimestampsSet()
    {
        var referral = Referral.Create(ReferrerId, ReferredUserId);

        referral.MarkCompleted();
        referral.MarkRewarded();

        referral.Status.Should().Be(ReferralStatus.Rewarded);
        referral.CompletedAtUtc.Should().NotBeNull();
        referral.RewardGrantedAtUtc.Should().NotBeNull();
    }
}
