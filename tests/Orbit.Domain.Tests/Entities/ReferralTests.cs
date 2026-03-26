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
