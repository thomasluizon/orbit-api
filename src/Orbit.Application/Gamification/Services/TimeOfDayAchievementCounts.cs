using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Gamification.Services;

public sealed record CompletionMinute(DateTime DateUtc, int Hour, int Minute, int Count);
public sealed record TimeOfDayCounts(int Early, int Night);

public static class TimeOfDayAchievementCounts
{
    public static async Task<TimeOfDayCounts> ReadAsync(IGenericRepository<HabitLog> repository,
        IReadOnlyCollection<Guid> habitIds, IReadOnlySet<string> earned, TimeZoneInfo zone, CancellationToken ct, TimeProvider? clock = null)
    {
        if (habitIds.Count == 0 || earned.Contains(AchievementDefinitions.EarlyBird) && earned.Contains(AchievementDefinitions.NightOwl))
            return new(0, 0);
        var createdAtUtcCutoff = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.AddDays(-90);
        var minutes = await repository.ProjectAsync(l => habitIds.Contains(l.HabitId) && l.CreatedAtUtc >= createdAtUtcCutoff,
            q => q.GroupBy(l => new { Date = l.CreatedAtUtc.Date, l.CreatedAtUtc.Hour, l.CreatedAtUtc.Minute })
                .Select(g => new CompletionMinute(g.Key.Date, g.Key.Hour, g.Key.Minute, g.Count())), ct);
        var early = 0;
        var night = 0;
        foreach (var minute in minutes)
        {
            var utc = DateTime.SpecifyKind(minute.DateUtc, DateTimeKind.Utc).AddHours(minute.Hour).AddMinutes(minute.Minute);
            var hour = TimeZoneInfo.ConvertTimeFromUtc(utc, zone).Hour;
            if (hour < 7) early += minute.Count;
            if (hour >= 22) night += minute.Count;
        }
        return new(early, night);
    }
}
