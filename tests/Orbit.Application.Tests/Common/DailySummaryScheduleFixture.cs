using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Common;

internal static class DailySummaryScheduleFixture
{
    internal static IReadOnlyList<HabitScheduleItem> Project(IReadOnlyList<Habit> habits, DateOnly day)
    {
        var lookup = habits.ToLookup(h => h.ParentHabitId);
        var facts = new HabitScheduleLogFacts(habits.SelectMany(h => h.Logs)
            .Where(l => !l.IsDeleted).GroupBy(l => new { l.HabitId, l.Date })
            .Select(g => new HabitScheduleLogDay(g.Key.HabitId, g.Key.Date,
                g.Count(l => l.Value > 0), g.Count(l => l.Value == 0), true)).ToList());
        var resolved = habits.Where(h => facts.ResolvedDates(h.Id).Contains(h.NextDueDate))
            .Select(h => h.Id).ToHashSet();
        var selected = HabitScheduleFilters.FilterScheduledHabits(lookup[null].OrderBy(h => h.Position),
            day, day, true, lookup, 1, resolved, facts);
        var context = new ScheduleMapContext(lookup, 1, IncludeOverdue: true, DateFrom: day, DateTo: day,
            ReferenceDate: day, UserToday: day, DueDateResolution: resolved, LogFacts: facts);
        return selected.Select(item => HabitScheduleFilters.MapToScheduleItem(
            item.habit, item.scheduledDates, item.isOverdue, context)).ToList();
    }
}
