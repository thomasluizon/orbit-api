namespace Orbit.Domain.Entities;

public class ProcessedPlayNotification : ProcessedExternalEvent
{
    public string MessageId { get; private set; } = "";
    public Guid? CouponUserId { get; private set; }
    public string? ConsumedCouponId { get; private set; }

    public void RecordConsumedCoupon(Guid userId, string couponId)
    {
        if (userId == Guid.Empty) throw new ArgumentException("A user id is required.", nameof(userId));
        if (string.IsNullOrWhiteSpace(couponId) || couponId.Length > 255) throw new ArgumentException("A coupon id is required.", nameof(couponId));
        CouponUserId = userId;
        ConsumedCouponId = couponId;
    }

    public void MarkCouponQueued()
    {
        CouponUserId = null;
        ConsumedCouponId = null;
    }

    private ProcessedPlayNotification() { }

    public static ProcessedPlayNotification Create(string messageId)
    {
        return new ProcessedPlayNotification
        {
            MessageId = messageId,
            ProcessedAtUtc = DateTime.UtcNow,
        };
    }
}
