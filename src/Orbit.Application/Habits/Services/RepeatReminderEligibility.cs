using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Services;

public sealed class RepeatReminderEligibility(
    IReadOnlyList<HabitScheduleLogDay> days, IReadOnlySet<Guid> resolvedDueDateIds)
{
    private readonly HabitScheduleLogFacts _facts = new(days);

    public bool CanSend(Habit habit, DateOnly date, int weekStartDay)
    {
        if (habit.IsCompleted || habit.IsDeleted || date < habit.StartDate
            || habit.EndDate is { } end && date > end)
            return false;
        if (habit.Schedule == HabitSchedule.General)
            return date >= habit.NextDueDate && !_facts.HasSkipped(habit.Id, date);
        if (habit.Schedule == HabitSchedule.Flexible)
            return !_facts.HasSkipped(habit.Id, date) && _facts.IsFlexibleDue(habit, date, weekStartDay);
        var resolvedDates = _facts.ResolvedDates(habit.Id);
        if (resolvedDates.Contains(date))
            return false;
        return HabitScheduleService.IsHabitDueOnDate(habit, date, weekStartDay)
            || HabitScheduleService.IsOverdueOnDate(habit, date, weekStartDay,
                resolvedDueDateIds.Contains(habit.Id), resolvedDates);
    }

    public static async Task<RepeatReminderEligibility> LoadAsync(IHabitScheduleLogReader reader,
        IReadOnlyCollection<Habit> habits, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var logFrom = from.AddDays(-Math.Min(AppConstants.MaxRangeDays, from.DayNumber));
        var yearStart = new DateOnly(from.Year, 1, 1);
        if (yearStart < logFrom)
            logFrom = yearStart;
        var ids = habits.Select(h => h.Id).ToArray();
        var days = await reader.ReadDaysAsync(ids, logFrom, to, ct);
        var oldDueIds = habits.Where(h => h.Schedule == HabitSchedule.Recurring
            && !h.IsBadHabit && h.NextDueDate < logFrom).Select(h => h.Id).ToArray();
        var resolvedDueIds = await reader.ReadResolvedDueDateIdsAsync(oldDueIds, ct);
        return new(days, resolvedDueIds);
    }
}
