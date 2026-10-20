using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

public class Referral : Entity
{
    public Guid ReferrerId { get; private set; }
    public Guid ReferredUserId { get; private set; }
    public ReferralStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public DateTime? RewardGrantedAtUtc { get; private set; }
    public ReferralRewardKind ReferrerReward { get; private set; }
    public ReferralRewardKind ReferredReward { get; private set; }
    public DateTime? ReferrerRewardDeliveredAtUtc { get; private set; }
    public DateTime? ReferredRewardDeliveredAtUtc { get; private set; }

    private Referral() { }

    public static Referral Create(Guid referrerId, Guid referredUserId)
    {
        if (referrerId == Guid.Empty || referredUserId == Guid.Empty || referrerId == referredUserId)
            throw new ArgumentException("Referrals require two distinct account ids.");
        return new Referral
        {
            ReferrerId = referrerId,
            ReferredUserId = referredUserId,
            Status = ReferralStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    public void MarkCompleted()
    {
        if (Status != ReferralStatus.Pending)
            return;
        Status = ReferralStatus.Completed;
        CompletedAtUtc = DateTime.UtcNow;
    }

    public void MarkRewarded(ReferralRewardKind referrerReward = ReferralRewardKind.Coupon,
        ReferralRewardKind referredReward = ReferralRewardKind.Coupon)
    {
        if (RewardGrantedAtUtc is not null)
            return;
        if (!Enum.IsDefined(referrerReward) || !Enum.IsDefined(referredReward)
            || referrerReward == ReferralRewardKind.None && referredReward == ReferralRewardKind.None)
            throw new ArgumentException("At least one valid referral reward is required.");
        MarkCompleted();
        ReferrerReward = referrerReward;
        ReferredReward = referredReward;
        Status = ReferralStatus.Rewarded;
        RewardGrantedAtUtc = DateTime.UtcNow;
    }

    public ReferralRewardKind GetReward(Guid userId) => userId == ReferrerId ? ReferrerReward
        : userId == ReferredUserId ? ReferredReward : throw new ArgumentException("Account is not part of this referral.");

    public bool IsRewardDelivered(Guid userId) => userId == ReferrerId ? ReferrerRewardDeliveredAtUtc is not null
        : userId == ReferredUserId ? ReferredRewardDeliveredAtUtc is not null : throw new ArgumentException("Account is not part of this referral.");

    public void MarkRewardDelivered(Guid userId)
    {
        if (RewardGrantedAtUtc is null || GetReward(userId) == ReferralRewardKind.None)
            throw new InvalidOperationException("Only a granted reward can be delivered.");
        if (userId == ReferrerId)
            ReferrerRewardDeliveredAtUtc ??= DateTime.UtcNow;
        else
            ReferredRewardDeliveredAtUtc ??= DateTime.UtcNow;
    }
}
