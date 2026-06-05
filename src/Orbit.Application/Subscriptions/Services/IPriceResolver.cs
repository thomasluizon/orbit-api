namespace Orbit.Application.Subscriptions.Services;

/// <summary>
/// Resolves the Stripe price ID for a billing interval and audience. The country
/// resolver collapses the world to a single <c>isBrazil</c> flag, so the price space
/// is a fixed 2×2 of currency (BRL/USD) × interval (monthly/yearly). Both the checkout
/// command and the plans query share this single mapping so the switch lives in one place.
/// </summary>
public interface IPriceResolver
{
    /// <summary>
    /// Returns the configured Stripe price ID for the given interval and audience.
    /// <paramref name="interval"/> must be <c>"monthly"</c> or <c>"yearly"</c>;
    /// callers validate the interval before reaching here.
    /// </summary>
    string Resolve(string interval, bool isBrazil);
}
