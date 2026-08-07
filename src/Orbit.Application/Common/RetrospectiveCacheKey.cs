namespace Orbit.Application.Common;

public static class RetrospectiveCacheKey
{
    public static string Build(Guid userId, string period, DateOnly dateFrom, string language)
    {
        var normalizedPeriod = period.ToLowerInvariant();
        var normalizedLanguage = string.IsNullOrEmpty(language) ? "en" : language;

        return $"retro:v2:{userId}:{normalizedPeriod}:{dateFrom}:{normalizedLanguage}";
    }
}
