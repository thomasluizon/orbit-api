namespace Orbit.Application.Common;

public static class PlaySubscriptionStateExtensions
{
    /// <summary>
    /// True only when Google reports an active subscription that is specifically the configured
    /// Orbit Pro product on a recognized (monthly or yearly) base plan. Guards entitlement against
    /// any other active subscription that could exist under the same Play package — an active
    /// subscription alone must never grant Pro.
    /// </summary>
    public static bool GrantsOrbitPro(this PlaySubscriptionState state, GooglePlaySettings settings) =>
        state.IsActive
        && state.Interval is not null
        && string.Equals(state.ProductId, settings.ProductId, StringComparison.Ordinal);
}
