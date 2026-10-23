using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Services;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat;

internal sealed record HabitTodaySnapshot(
    IReadOnlySet<Guid> TodayIds,
    IReadOnlySet<Guid> OverdueIds,
    IReadOnlySet<Guid> DoneTodayIds, string? SourceRevision = null)
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
            || habit.Schedule == HabitSchedule.OneTime).Select(habit => habit.Id).ToArray();
        var logDays = await reader.ReadDaysAsync(candidateIds, logFrom, date, cancellationToken);
        var logFacts = new HabitScheduleLogFacts(logDays);
        var active = habits.Where(habit => !habit.IsCompleted).ToList();
        var dueDateResolution = active
            .Where(habit => habit.NextDueDate >= logFrom
                && habit.NextDueDate <= date
                && logFacts.ResolvedDates(habit.Id).Contains(habit.NextDueDate))
            .Select(habit => habit.Id)
            .ToHashSet();
        var oldDueDateIds = active
            .Where(habit => habit.Schedule == HabitSchedule.Recurring
                && !habit.IsBadHabit
                && habit.NextDueDate < logFrom)
            .Select(habit => habit.Id)
            .ToArray();
        if (oldDueDateIds.Length > 0)
        {
            var olderResolutions = await reader.ReadResolvedDueDateIdsAsync(oldDueDateIds, cancellationToken);
            dueDateResolution.UnionWith(olderResolutions);
        }
        return Build(habits, date, weekStartDay, logDays, dueDateResolution) with
        { SourceRevision = ReadRevision.Hash(logDays.OrderBy(l => l.HabitId).ThenBy(l => l.Date)) };
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
            habit.IsCompleted && habit.Schedule == HabitSchedule.OneTime
            && logFacts.HasCompleted(habit.Id, today, today)).ToList();
        var candidates = active.Where(habit => habit.Schedule != HabitSchedule.General);
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
