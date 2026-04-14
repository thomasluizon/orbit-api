using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Subscriptions;

internal static class SubscriptionPricingCountryResolver
{
    public static async Task<string?> ResolveCountryCodeAsync(
        User user,
        string? requestCountryCode,
        string? ipAddress,
        IGeoLocationService geoLocationService,
        CancellationToken cancellationToken)
    {
        var explicitCountryCode = NormalizeCountryCode(requestCountryCode);
        if (explicitCountryCode is not null)
            return explicitCountryCode;

        if (HasBrazilProfileHint(user))
            return "BR";

        var geoCountryCode = await geoLocationService.GetCountryCodeAsync(ipAddress, cancellationToken);
        return NormalizeCountryCode(geoCountryCode);
    }

    private static bool HasBrazilProfileHint(User user)
    {
        return LocaleHelper.IsPortuguese(user.Language) || TimeZoneHelper.IsBrazilTimeZone(user.TimeZone);
    }

    private static string? NormalizeCountryCode(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
            return null;

        var normalized = countryCode.Trim().ToUpperInvariant();
        return normalized.Length == 2 && normalized.All(char.IsLetter)
            ? normalized
            : null;
    }
}
