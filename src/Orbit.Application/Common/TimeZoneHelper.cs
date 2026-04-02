using Microsoft.Extensions.Logging;

namespace Orbit.Application.Common;

public static class TimeZoneHelper
{
    public static TimeZoneInfo FindTimeZone(string? timeZoneId, ILogger? logger = null, Guid? userId = null)
    {
        if (string.IsNullOrEmpty(timeZoneId))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch
        {
            logger?.LogWarning("Unknown timezone {TimeZone} for user {UserId}, falling back to UTC",
                timeZoneId, userId);
            return TimeZoneInfo.Utc;
        }
    }
}
