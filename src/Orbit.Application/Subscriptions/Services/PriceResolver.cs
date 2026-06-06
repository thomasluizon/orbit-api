using Microsoft.Extensions.Options;
using Orbit.Application.Common;

namespace Orbit.Application.Subscriptions.Services;

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
