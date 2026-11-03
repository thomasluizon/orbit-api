using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public sealed class PendingCouponCancellation : Entity
{
    public Guid UserId { get; private set; }
    public string CouponId { get; private set; } = string.Empty;
    public int Attempts { get; private set; }
    public DateTime NextAttemptAtUtc { get; private set; }

    private PendingCouponCancellation() { }

    public static PendingCouponCancellation Create(Guid userId, string couponId)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A coupon cancellation requires an owner.");
        ArgumentException.ThrowIfNullOrWhiteSpace(couponId);
        if (couponId.Length > 255)
            throw new ArgumentException("Coupon identifier exceeds its storage limit.");
        return new PendingCouponCancellation { UserId = userId, CouponId = couponId, NextAttemptAtUtc = DateTime.UtcNow };
    }

    public void RecordFailure(DateTime attemptedAtUtc)
    {
        if (attemptedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Cancellation attempts require a UTC instant.");
        Attempts++;
        NextAttemptAtUtc = attemptedAtUtc.AddMinutes(Math.Min(60, Attempts * 5));
    }
}
