using Orbit.Domain.Enums;

namespace Orbit.Application.Common;

public static class SubscriptionSourceExtensions
{
    /// <summary>Maps the entitlement source to its API contract value ("stripe" | "play"), or null when unset.</summary>
    public static string? ToApiValue(this SubscriptionSource? source) => source switch
    {
        SubscriptionSource.Stripe => "stripe",
        SubscriptionSource.GooglePlay => "play",
        _ => null,
    };
}
