using Orbit.Application.Habits.Queries;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Services;

public static class PeriodAnalyticsCalculator
{
    public static readonly IReadOnlyList<string> Metrics =
        ["positiveLogRecords", "distinctLoggedDates", "metScheduledDates", "scheduledDates"];

    public static int Value(PeriodHabitMetrics metrics, string metric) => metric switch
    {
        "positiveLogRecords" => metrics.PositiveLogRecords,
        "distinctLoggedDates" => metrics.DistinctLoggedDates,
        "metScheduledDates" => metrics.MetScheduledDates,
        "scheduledDates" => metrics.ScheduledDates,
        _ => throw new ArgumentException("Unsupported count metric.", nameof(metric))
    };

    public static (PeriodHabitMetrics Metrics, CompletionSeries Series) Calculate(
        IReadOnlyList<Habit> habits, ReadWindow window, TimeZoneInfo zone, int weekStartDay)
    {
        var scheduled = new Dictionary<DateOnly, int>();
        var met = new Dictionary<DateOnly, int>();
        var loggedDates = new HashSet<DateOnly>();
        var records = 0;
        foreach (var habit in habits)
        {
            var logs = habit.Logs.Where(l => !l.IsDeleted && l.Value > 0
                && l.Date >= window.From && l.Date <= window.To).ToList();
            records += logs.Count;
            var positiveDates = logs.Select(l => l.Date).ToHashSet();
            loggedDates.UnionWith(positiveDates);
            if (habit.IsGeneral || habit.IsBadHabit)
                continue;
            var dates = new List<DateOnly>();
            for (var start = window.From; start <= window.To;)
            {
                var end = DateOnly.FromDayNumber(Math.Min(start.DayNumber + AppConstants.MaxRangeDays, window.To.DayNumber));
                dates.AddRange(HabitScheduleService.GetHistoricalScheduledDates(habit, start, end, zone, weekStartDay));
                if (end == window.To) break;
                start = end.AddDays(1);
            }
            foreach (var date in dates.Distinct())
            {
                scheduled[date] = scheduled.GetValueOrDefault(date) + 1;
                if (positiveDates.Contains(date))
                    met[date] = met.GetValueOrDefault(date) + 1;
            }
        }
        var run = 0;
        var best = 0;
        var current = 0;
        var points = new List<CompletionSeriesPoint>();
        var daily = window.To.DayNumber - window.From.DayNumber < 31;
        for (var day = window.From; day <= window.To;)
        {
            var end = daily ? day : DateOnly.FromDayNumber(Math.Min(window.To.DayNumber,
                day.DayNumber + 6 - ((7 + (int)day.DayOfWeek - weekStartDay) % 7)));
            var due = 0;
            var done = 0;
            for (var index = day.DayNumber; index <= end.DayNumber; index++)
            {
                var date = DateOnly.FromDayNumber(index);
                due += scheduled.GetValueOrDefault(date);
                done += met.GetValueOrDefault(date);
                run = loggedDates.Contains(date) ? run + 1 : 0;
                best = Math.Max(best, run);
                current = run;
            }
            points.Add(new(day, end, due, done, due == 0 ? null : (int)Math.Round(100m * done / due)));
            if (end == window.To) break;
            day = end.AddDays(1);
        }
        var denominator = scheduled.Values.Sum();
        var numerator = met.Values.Sum();
        return (new(window, records, loggedDates.Count, numerator, denominator,
            denominator == 0 ? null : 100m * numerator / denominator, current, best),
            new(daily ? "day" : "week", points));
    }
}
