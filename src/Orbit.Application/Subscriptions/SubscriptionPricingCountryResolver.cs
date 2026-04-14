using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Subscriptions;

internal static class SubscriptionPricingCountryResolver
{
    private static readonly HashSet<string> BrazilTimeZones = new(StringComparer.OrdinalIgnoreCase)
    {
        "America/Araguaina",
        "America/Bahia",
        "America/Belem",
        "America/Boa_Vista",
        "America/Campo_Grande",
        "America/Cuiaba",
        "America/Eirunepe",
        "America/Fortaleza",
        "America/Maceio",
        "America/Manaus",
        "America/Noronha",
        "America/Porto_Velho",
        "America/Recife",
        "America/Rio_Branco",
        "America/Santarem",
        "America/Sao_Paulo",
        "Brazil/Acre",
        "Brazil/DeNoronha",
        "Brazil/East",
        "Brazil/West"
    };

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
        return LocaleHelper.IsPortuguese(user.Language) || IsBrazilTimeZone(user.TimeZone);
    }

    private static bool IsBrazilTimeZone(string? timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone))
            return false;

        return BrazilTimeZones.Contains(timeZone.Trim());
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
