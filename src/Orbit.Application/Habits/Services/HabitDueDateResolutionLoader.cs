using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Services;

internal static class HabitDueDateResolutionLoader
{
    public static async Task<IReadOnlySet<Guid>> LoadAsync(
        IGenericRepository<HabitLog> habitLogRepository,
        IEnumerable<Habit> habits,
        DateOnly loadedLogFrom,
        CancellationToken cancellationToken)
    {
        var habitList = habits.ToList();
        var resolvedHabitIds = habitList
            .Where(habit => IsResolvedInLoadedLogs(habit))
            .Select(habit => habit.Id)
            .ToHashSet();
        var missingDueDates = habitList
            .Where(habit => habit.FrequencyUnit is not null
                && !habit.IsFlexible
                && !habit.IsBadHabit
                && habit.DueDate < loadedLogFrom
                && !resolvedHabitIds.Contains(habit.Id))
            .ToDictionary(habit => habit.Id, habit => habit.DueDate);

        if (missingDueDates.Count == 0)
            return resolvedHabitIds;

        var habitIds = missingDueDates.Keys.ToHashSet();
        var dueDates = missingDueDates.Values.ToHashSet();
        var resolutionLogs = await habitLogRepository.FindAsync(
            log => habitIds.Contains(log.HabitId) && dueDates.Contains(log.Date),
            cancellationToken);

        foreach (var log in resolutionLogs)
        {
            if (!log.IsDeleted
                && log.Value >= 0
                && missingDueDates.TryGetValue(log.HabitId, out var dueDate)
                && log.Date == dueDate)
            {
                resolvedHabitIds.Add(log.HabitId);
            }
        }

        return resolvedHabitIds;
    }

    private static bool IsResolvedInLoadedLogs(Habit habit) =>
        habit.Logs.Any(log => !log.IsDeleted && log.Date == habit.DueDate && log.Value >= 0);
}
