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

    private Referral() { }

    public static Referral Create(Guid referrerId, Guid referredUserId)
    {
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
        Status = ReferralStatus.Completed;
        CompletedAtUtc = DateTime.UtcNow;
    }

    public void MarkRewarded()
    {
        Status = ReferralStatus.Rewarded;
        RewardGrantedAtUtc = DateTime.UtcNow;
    }
}
