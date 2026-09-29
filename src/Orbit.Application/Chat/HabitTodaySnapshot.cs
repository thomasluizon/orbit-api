using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat;

internal sealed record HabitTodaySnapshot(
    IReadOnlySet<Guid> TodayIds,
    IReadOnlySet<Guid> OverdueIds,
    IReadOnlySet<Guid> DoneTodayIds)
{
    internal static HabitTodaySnapshot Build(
        IReadOnlyList<Habit> habits,
        DateOnly today,
        int weekStartDay,
        IReadOnlyList<HabitScheduleLogDay> logDays,
        IReadOnlySet<Guid> resolvedDueDateIds)
    {
        var logFacts = new HabitScheduleLogFacts(logDays);
        var active = habits.Where(habit => !habit.IsCompleted).ToList();
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
                .Select(item => item.habit.Id).ToHashSet(),
            scheduled.Where(item => item.isOverdue).Select(item => item.habit.Id).ToHashSet(),
            active.Where(habit => !habit.IsBadHabit && logFacts.HasCompleted(habit.Id, today, today))
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
