using Microsoft.Extensions.Options;
using Orbit.Application.Common;

namespace Orbit.Application.Subscriptions.Services;

// Config stays flat (MonthlyPriceIdBrl / YearlyPriceIdBrl / MonthlyPriceIdUsd / YearlyPriceIdUsd)
// rather than the nested Stripe:Prices:BRL:Annual shape suggested in #78: the audience collapses
// to one isBrazil bool, so the price space is a fixed 2×2 that this resolver fully hides from both
// callers. Nesting would buy no caller-side clarity and would churn StripeSettings, the Render env
// var names, and the existing unit tests for zero behavioral gain.
public sealed class PriceResolver(IOptions<StripeSettings> stripeSettings) : IPriceResolver
{
    private readonly StripeSettings _settings = stripeSettings.Value;

    public string Resolve(string interval, bool isBrazil) => (interval, isBrazil) switch
    {
        ("monthly", true) => _settings.MonthlyPriceIdBrl,
        ("monthly", false) => _settings.MonthlyPriceIdUsd,
        ("yearly", true) => _settings.YearlyPriceIdBrl,
        ("yearly", false) => _settings.YearlyPriceIdUsd,
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, "Interval must be 'monthly' or 'yearly'.")
    };
}
