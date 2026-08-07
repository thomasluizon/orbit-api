namespace Orbit.Application.Common;

public static class RetrospectiveCacheKey
{
    public static string Build(Guid userId, string period, DateOnly dateFrom, string language) =>
        $"retro:v2:{userId}:{period}:{dateFrom}:{language}";
}
