using Orbit.Application.Chat;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Services;

public static class DailySummaryReadProjection
{
    public static (DaySummaryCard Card, string Revision) Project(IReadOnlyList<Habit> habits,
        DateOnly from, DateOnly to, TimeZoneInfo zone, int weekStartDay, int currentStreak)
    {
        var (period, _) = PeriodAnalyticsCalculator.Calculate(habits.Where(h => h.ParentHabitId is null).ToList(),
            new(from, to), zone, weekStartDay);
        var days = habits.SelectMany(h => h.Logs.Where(l => !l.IsDeleted).GroupBy(l => l.Date)
            .Select(group => new HabitScheduleLogDay(h.Id, group.Key, group.Count(l => l.Value > 0),
                group.Count(l => l.Value == 0), true))).ToList();
        var resolvedDue = habits.Where(h => h.Logs.Any(l => !l.IsDeleted && l.Date == h.NextDueDate))
            .Select(h => h.Id).ToHashSet();
        var snapshot = HabitTodaySnapshot.Build(habits, from, weekStartDay, days, resolvedDue);
        var rows = HabitListCardBuilder.Build(habits, from, "today", snapshot, supportsDoneStatus: true).Items;
        var card = new DaySummaryCard(from, period.ScheduledDates, period.MetScheduledDates,
            period.CompletionRate.HasValue ? (int)Math.Round(period.CompletionRate.Value) : null,
            snapshot.OverdueIds.Count, currentStreak, Rows: rows, TotalRows: rows.Count, DateTo: to);
        var revision = ReadRevision.Hash(new
        {
            period,
            rows,
            Sources = habits.OrderBy(h => h.Id).Select(h => new
            {
                h.Id,
                h.UpdatedAtUtc,
                Logs = h.Logs.Where(l => !l.IsDeleted).OrderBy(l => l.Date).ThenBy(l => l.Value).Select(l => new { l.Date, l.Value })
            })
        });
        return (card, revision);
    }
}
