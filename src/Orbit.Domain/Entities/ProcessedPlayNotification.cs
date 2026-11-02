namespace Orbit.Domain.Entities;

public class ProcessedPlayNotification : ProcessedExternalEvent
{
    public string MessageId { get; private set; } = "";
    public Guid? ProcessingLeaseId { get; private set; }
    public DateTime? ProcessingLeaseExpiresAtUtc { get; private set; }

    public static ProcessedPlayNotification Claim(string messageId, DateTime nowUtc, TimeSpan leaseDuration)
    {
        var notification = Create(messageId);
        notification.SetLease(nowUtc, leaseDuration);
        return notification;
    }

    public bool TryReclaim(DateTime nowUtc, TimeSpan leaseDuration)
    {
        if (ProcessingLeaseId is null || ProcessingLeaseExpiresAtUtc > nowUtc) return false;
        SetLease(nowUtc, leaseDuration);
        return true;
    }

    private void SetLease(DateTime nowUtc, TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (nowUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("A UTC instant is required.", nameof(nowUtc));
        var expiresAtUtc = nowUtc.Add(leaseDuration);
        ProcessingLeaseId = Guid.NewGuid();
        ProcessingLeaseExpiresAtUtc = expiresAtUtc;
    }

    public void Complete(Guid leaseId, DateTime processedAtUtc)
    {
        if (ProcessingLeaseId is null || ProcessingLeaseId != leaseId)
            throw new InvalidOperationException("The processing lease is no longer owned.");
        if (processedAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("A UTC instant is required.", nameof(processedAtUtc));
        ProcessingLeaseId = null;
        ProcessingLeaseExpiresAtUtc = null;
        ProcessedAtUtc = processedAtUtc;
    }

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
        if (string.IsNullOrWhiteSpace(messageId) || messageId.Length > 255)
            throw new ArgumentException("A message id is required.", nameof(messageId));
        return new ProcessedPlayNotification
        {
            MessageId = messageId,
            ProcessedAtUtc = DateTime.UtcNow,
        };
    }
}
