using Microsoft.Extensions.Logging;

namespace Orbit.Application.Common;

public static class TimeZoneHelper
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

    public static TimeZoneInfo FindTimeZone(string? timeZoneId, ILogger? logger = null, Guid? userId = null)
    {
        if (string.IsNullOrEmpty(timeZoneId))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException ex)
        {
            logger?.LogWarning(ex, "Unknown timezone {TimeZone} for user {UserId}, falling back to UTC",
                timeZoneId, userId);
            return TimeZoneInfo.Utc;
        }
    }

    public static bool IsBrazilTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return false;

        return BrazilTimeZones.Contains(timeZoneId.Trim());
    }
}
