using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Services;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat;

internal sealed record HabitTodaySnapshot(
    IReadOnlySet<Guid> TodayIds,
    IReadOnlySet<Guid> OverdueIds,
    IReadOnlySet<Guid> DoneTodayIds)
{
    internal static async Task<HabitTodaySnapshot> ReadAsync(
        IReadOnlyList<Habit> habits,
        DateOnly date,
        int weekStartDay,
        IHabitScheduleLogReader reader,
        CancellationToken cancellationToken)
    {
        var logFrom = date.AddDays(-AppConstants.MaxRangeDays);
        var candidateIds = habits.Where(habit => !habit.IsCompleted
            || habit.FrequencyUnit is null && !habit.IsGeneral).Select(habit => habit.Id).ToArray();
        var logDays = await reader.ReadDaysAsync(candidateIds, logFrom, date, cancellationToken);
        var logFacts = new HabitScheduleLogFacts(logDays);
        var active = habits.Where(habit => !habit.IsCompleted).ToList();
        var dueDateResolution = active
            .Where(habit => habit.DueDate >= logFrom
                && habit.DueDate <= date
                && logFacts.ResolvedDates(habit.Id).Contains(habit.DueDate))
            .Select(habit => habit.Id)
            .ToHashSet();
        var oldDueDateIds = active
            .Where(habit => habit.FrequencyUnit is not null
                && !habit.IsFlexible
                && !habit.IsBadHabit
                && habit.DueDate < logFrom)
            .Select(habit => habit.Id)
            .ToArray();
        if (oldDueDateIds.Length > 0)
        {
            var olderResolutions = await reader.ReadResolvedDueDateIdsAsync(oldDueDateIds, cancellationToken);
            dueDateResolution.UnionWith(olderResolutions);
        }
        return Build(habits, date, weekStartDay, logDays, dueDateResolution);
    }

    internal static HabitTodaySnapshot Build(
        IReadOnlyList<Habit> habits,
        DateOnly today,
        int weekStartDay,
        IReadOnlyList<HabitScheduleLogDay> logDays,
        IReadOnlySet<Guid> resolvedDueDateIds)
    {
        var logFacts = new HabitScheduleLogFacts(logDays);
        var active = habits.Where(habit => !habit.IsCompleted).ToList();
        var completedToday = habits.Where(habit =>
            habit.IsCompleted && habit.FrequencyUnit is null && !habit.IsGeneral
            && logFacts.HasCompleted(habit.Id, today, today)).ToList();
        var candidates = active.Where(habit => !habit.IsGeneral);
        var emptyLookup = Array.Empty<Habit>().ToLookup(habit => habit.ParentHabitId);
        var scheduled = HabitScheduleFilters.FilterScheduledHabits(
            candidates,
            today,
            today,
            includeOverdue: true,
            emptyLookup,
            weekStartDay,
            resolvedDueDateIds,
            logFacts);

        return new HabitTodaySnapshot(
            scheduled.Where(item => !item.isOverdue
                && HabitScheduleService.WasScheduledOnDate(item.habit, today, weekStartDay))
                .Select(item => item.habit.Id)
                .Concat(completedToday.Select(habit => habit.Id)).ToHashSet(),
            scheduled.Where(item => item.isOverdue).Select(item => item.habit.Id).ToHashSet(),
            active.Concat(completedToday)
                .Where(habit => !habit.IsBadHabit && logFacts.HasCompleted(habit.Id, today, today))
                .Select(habit => habit.Id).ToHashSet());
    }

    internal static HabitTodaySnapshot FromLoadedHabits(IReadOnlyList<Habit> habits, DateOnly today)
    {
        var days = habits.SelectMany(habit => habit.Logs
                .Where(log => !log.IsDeleted && log.Date == today)
                .GroupBy(log => log.Date)
                .Select(group => new HabitScheduleLogDay(
                    habit.Id,
                    group.Key,
                    group.Count(log => log.Value > 0 && log.IsSlip != true),
                    group.Count(log => log.Value == 0),
                    true)))
            .ToList();
        return Build(habits, today, 1, days, new HashSet<Guid>());
    }
}
