using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Gamification.Services;

public sealed record CompletionDay(Guid HabitId, DateOnly Date, int Count);

public static class HabitStreakLogLoader
{
    public static async Task<IReadOnlyDictionary<Guid, List<HabitMetricLog>>> LoadAsync(
        IGenericRepository<HabitLog> repository, IReadOnlyList<Habit> habits,
        DateOnly today, int weekStartDay, TimeZoneInfo zone, CancellationToken ct)
    {
        var result = habits.ToDictionary(h => h.Id, _ => new List<HabitMetricLog>());
        var byId = habits.ToDictionary(h => h.Id);
        var pending = habits.ToList();
        var window = 64;
        DateOnly? previous = null;
        while (pending.Count > 0)
        {
            var from = today.AddDays(-window);
            var ids = pending.Select(h => h.Id).ToArray();
            var rows = await repository.ProjectAsync(l => ids.Contains(l.HabitId) && l.Date >= from
                    && (previous == null || l.Date < previous) && l.Value > 0,
                q => q.GroupBy(l => new { l.HabitId, l.Date })
                    .Select(g => new CompletionDay(g.Key.HabitId, g.Key.Date, g.Count())), ct);
            foreach (var row in rows)
            {
                var copies = byId[row.HabitId].Schedule == Domain.Enums.HabitSchedule.Flexible ? row.Count : 1;
                for (var index = 0; index < copies; index++) result[row.HabitId].Add(new(row.HabitId, row.Date, 1, false));
            }
            pending = pending.Where(h => NeedsEarlier(h, result[h.Id], today, weekStartDay, zone, from, window)).ToList();
            previous = from;
            window = Math.Min(window * 2, AppConstants.StreakLogWindowDays);
        }
        return result;
    }

    private static bool NeedsEarlier(Habit habit, IReadOnlyCollection<HabitMetricLog> logs,
        DateOnly today, int weekStartDay, TimeZoneInfo zone, DateOnly from, int window)
    {
        if (window >= AppConstants.StreakLogWindowDays) return false;
        HabitMetricsCalculator.CalculateProjected(habit, logs, today, weekStartDay, zone, out var neededFrom);
        return habit.LongestRecordedStreak == null || neededFrom < from;
    }
}
