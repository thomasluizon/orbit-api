using Orbit.Application.Chat;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Services;

public static class DailySummaryReadProjection
{
    public static (DaySummaryCard Card, string Revision) Project(IReadOnlyList<Habit> habits,
        DateOnly from, DateOnly to, TimeZoneInfo zone, int weekStartDay, int currentStreak,
        IReadOnlyList<HabitScheduleItem> scheduleItems)
    {
        var (period, _) = PeriodAnalyticsCalculator.Calculate(habits.Where(h => h.ParentHabitId is null).ToList(),
            new(from, to), zone, weekStartDay);
        var rows = new List<HabitListCardItem>();
        foreach (var item in scheduleItems)
        {
            rows.Add(new(item.Id.ToString(), item.Title, item.Emoji, 0, item.IsBadHabit,
                Status(item.IsLoggedInRange, item.IsBadHabit, item.IsOverdue), item.StartDate,
                item.ParentHabitId, item.ParentTitle, item.Tags));
            AppendChildren(item.Children, 1, item.Title, rows);
        }
        var due = from == to ? rows.Count : period.ScheduledDates;
        var done = from == to ? rows.Count(row => row.Status == HabitListCardBuilder.StatusDone) : period.MetScheduledDates;
        var rate = from == to ? due == 0 ? (int?)null : (int)Math.Round(100m * done / due)
            : period.CompletionRate.HasValue ? (int)Math.Round(period.CompletionRate.Value) : (int?)null;
        var card = new DaySummaryCard(from, due, done, rate,
            rows.Count(row => row.Status == HabitListCardBuilder.StatusOverdue), currentStreak,
            Rows: rows, TotalRows: rows.Count, DateTo: to);
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

    private static string Status(bool logged, bool badHabit, bool overdue) =>
        logged && !badHabit ? HabitListCardBuilder.StatusDone
        : overdue ? HabitListCardBuilder.StatusOverdue : HabitListCardBuilder.StatusToday;

    private static void AppendChildren(IReadOnlyList<HabitScheduleChildItem> children, int depth,
        string parentTitle, List<HabitListCardItem> rows)
    {
        foreach (var child in children)
        {
            rows.Add(new(child.Id.ToString(), child.Title, child.Emoji, depth, child.IsBadHabit,
                Status(child.IsLoggedInRange, child.IsBadHabit, child.IsOverdue), child.StartDate,
                child.ParentHabitId, parentTitle, child.Tags));
            AppendChildren(child.Children, depth + 1, child.Title, rows);
        }
    }
}
