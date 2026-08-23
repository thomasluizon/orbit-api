using Orbit.Domain.Enums;

namespace Orbit.Application.Common;

public static class SubscriptionLapseReasonExtensions
{
    public static string? ToApiValue(this SubscriptionLapseReason? reason) => reason switch
    {
        SubscriptionLapseReason.Canceled => "canceled",
        SubscriptionLapseReason.PaymentFailed => "payment_failed",
        SubscriptionLapseReason.Expired => "expired",
        _ => null,
    };
}
