using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

internal sealed class HabitScheduleLogFacts(IEnumerable<HabitScheduleLogDay> days)
{
    private readonly ILookup<Guid, HabitScheduleLogDay> _byHabit = days.ToLookup(day => day.HabitId);

    internal IReadOnlySet<DateOnly> ResolvedDates(Guid habitId) =>
        _byHabit[habitId]
            .Where(day => day.CompletedCount > 0 || day.SkippedCount > 0)
            .Select(day => day.Date)
            .ToHashSet();

    internal bool HasCompleted(Guid habitId, DateOnly from, DateOnly to) =>
        _byHabit[habitId].Any(day => day.Date >= from && day.Date <= to && day.CompletedCount > 0);

    internal bool HasLog(Guid habitId, DateOnly from, DateOnly to) =>
        _byHabit[habitId].Any(day => day.Date >= from && day.Date <= to && day.HasLog);

    internal bool IsFlexibleDue(Habit habit, DateOnly date, int weekStartDay)
    {
        if (!habit.IsFlexible || habit.FrequencyUnit is null || date < habit.DueDate
            || !HabitScheduleService.IsActiveIntervalWeek(habit, date, weekStartDay))
            return false;

        var from = HabitScheduleService.GetWindowStart(habit, date, weekStartDay);
        var to = HabitScheduleService.GetWindowEnd(habit, date, weekStartDay);
        var days = _byHabit[habit.Id].Where(day => day.Date >= from && day.Date <= to);
        var completed = days.Sum(day => day.CompletedCount);
        var skipped = days.Sum(day => day.SkippedCount);
        return completed < Math.Max(0, (habit.FrequencyQuantity ?? 1) - skipped);
    }
}
