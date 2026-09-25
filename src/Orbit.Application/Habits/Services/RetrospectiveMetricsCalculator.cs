using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;

namespace Orbit.Application.Habits.Services;

/// <summary>
/// Computes the structured, deterministic metrics shown on the retrospective dashboard
/// (completion rates, streak echo, active/period days, per-weekday consistency, and the
/// top / needs-attention habit lists) from the habits and in-range logs already loaded by
/// the query handler. The AI narrative is produced separately by <see cref="IRetrospectiveService"/>.
/// </summary>
public static class RetrospectiveMetricsCalculator
{
    private const int MaxHabitStats = 3;

    private static readonly DayOfWeek[] WeekOrder =
    [
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
        DayOfWeek.Saturday,
        DayOfWeek.Sunday
    ];

    public static RetrospectiveMetrics Compute(
        List<Habit> habits,
        DateOnly dateFrom,
        DateOnly dateTo,
        int currentStreak,
        int bestStreak,
        int weekStartDay = 1)
    {
        return Compute(
            habits,
            dateFrom,
            dateTo,
            currentStreak,
            bestStreak,
            weekStartDay,
            (habit, from, to) => HabitScheduleService.GetScheduledDates(
                habit, from, to, weekStartDay,
                habit.FrequencyUnit is not null && !habit.IsFlexible
                    ? habit.ScheduledStartDate ?? habit.DueDate
                    : null));
    }

    /// <summary>
    /// Computes closed-period metrics from the schedule that applied during the resolved window,
    /// independent of the habit's later forward-advanced due date.
    /// </summary>
    public static RetrospectiveMetrics ComputeHistorical(
        List<Habit> habits,
        DateOnly dateFrom,
        DateOnly dateTo,
        int currentStreak,
        int bestStreak,
        TimeZoneInfo userTimeZone,
        int weekStartDay = 1)
    {
        return Compute(
            habits,
            dateFrom,
            dateTo,
            currentStreak,
            bestStreak,
            weekStartDay,
            (habit, from, to) => HabitScheduleService.GetHistoricalScheduledDates(
                habit,
                from,
                to,
                userTimeZone,
                weekStartDay));
    }

    private static RetrospectiveMetrics Compute(
        List<Habit> habits,
        DateOnly dateFrom,
        DateOnly dateTo,
        int currentStreak,
        int bestStreak,
        int weekStartDay,
        Func<Habit, DateOnly, DateOnly, List<DateOnly>> resolveScheduledDates)
    {
        var trackedHabits = habits.Where(h => h.ParentHabitId is null).ToList();

        var totalCompletions = 0;
        var totalMet = 0;
        var totalScheduled = 0;
        var badHabitSlips = 0;
        var stats = new List<RetrospectiveHabitStat>();
        var weekdayScheduled = new int[7];
        var weekdayCompleted = new int[7];
        var dailyScheduled = new int[dateTo.DayNumber - dateFrom.DayNumber + 1];
        var dailyCompleted = new int[dailyScheduled.Length];

        foreach (var habit in trackedHabits)
        {
            var scheduledDates = resolveScheduledDates(habit, dateFrom, dateTo);
            var completedCount = habit.Logs.Count(l => !l.IsDeleted
                && l.Date >= dateFrom
                && l.Date <= dateTo
                && l.Value > 0);

            if (scheduledDates.Count == 0 && completedCount == 0)
                continue;

            if (habit.IsBadHabit)
            {
                badHabitSlips += completedCount;
                continue;
            }

            if (habit.FrequencyUnit is not null && !habit.IsFlexible)
            {
                var recurrenceStart = habit.ScheduledStartDate ?? habit.DueDate;
                var occurrenceDates = ResolveOccurrences(
                    habit, recurrenceStart, dateTo, resolveScheduledDates).ToHashSet();
                var unresolvedDates = new SortedSet<DateOnly>(occurrenceDates);
                var creditedDates = scheduledDates.ToHashSet();
                foreach (var log in habit.Logs.Where(l => !l.IsDeleted
                    && l.Date <= dateTo).OrderBy(l => l.Date))
                {
                    if (occurrenceDates.Contains(log.Date))
                    {
                        unresolvedDates.Remove(log.Date);
                        continue;
                    }

                    if (log.Value <= 0 || unresolvedDates.Count == 0 || unresolvedDates.Min >= log.Date)
                        continue;

                    unresolvedDates.Remove(unresolvedDates.Min);
                    if (log.Date >= dateFrom && creditedDates.Add(log.Date))
                        scheduledDates.Add(log.Date);
                }
            }

            totalScheduled += scheduledDates.Count;
            totalCompletions += completedCount;
            var metCount = AccumulateWeekdayConsistency(
                habit, scheduledDates, weekdayScheduled, weekdayCompleted,
                dailyScheduled, dailyCompleted, dateFrom);
            totalMet += metCount;
            stats.Add(BuildHabitStat(habit, scheduledDates.Count, completedCount, metCount));
        }

        var completionRate = Percent(totalMet, totalScheduled);
        var activeDays = CountActiveDays(habits, dateFrom, dateTo);
        var periodDays = dateTo.DayNumber - dateFrom.DayNumber + 1;
        var weeklyConsistency = BuildWeeklyConsistency(weekdayScheduled, weekdayCompleted);

        var recurringStats = stats.Where(s => !s.IsOneTime);

        var topHabits = recurringStats
            .OrderByDescending(s => s.CompletionRate)
            .ThenByDescending(s => s.CompletedCount)
            .Take(MaxHabitStats)
            .ToList();

        var needsAttention = recurringStats
            .Where(s => s.CompletionRate < 100)
            .OrderBy(s => s.CompletionRate)
            .ThenByDescending(s => s.ScheduledCount)
            .Take(MaxHabitStats)
            .ToList();

        return new RetrospectiveMetrics(
            completionRate,
            totalCompletions,
            totalScheduled,
            activeDays,
            periodDays,
            currentStreak,
            bestStreak,
            badHabitSlips,
            weeklyConsistency,
            topHabits,
            needsAttention,
            BuildCompletionSeries(dateFrom, dateTo, weekStartDay, dailyScheduled, dailyCompleted));
    }

    private static List<DateOnly> ResolveOccurrences(
        Habit habit, DateOnly recurrenceStart, DateOnly dateTo,
        Func<Habit, DateOnly, DateOnly, List<DateOnly>> resolveScheduledDates)
    {
        var occurrences = new List<DateOnly>();
        for (var from = recurrenceStart; from <= dateTo;)
        {
            var to = DateOnly.FromDayNumber(Math.Min(
                from.DayNumber + AppConstants.MaxRangeDays, dateTo.DayNumber));
            occurrences.AddRange(resolveScheduledDates(habit, from, to));
            if (to == dateTo)
                break;
            from = to.AddDays(1);
        }

        return occurrences;
    }

    private static int AccumulateWeekdayConsistency(
        Habit habit, List<DateOnly> scheduledDates, int[] weekdayScheduled, int[] weekdayCompleted,
        int[] dailyScheduled, int[] dailyCompleted, DateOnly dateFrom)
    {
        var metCount = 0;
        var completedDates = habit.Logs
            .Where(l => !l.IsDeleted && l.Value > 0)
            .Select(l => l.Date)
            .ToHashSet();

        foreach (var date in scheduledDates)
        {
            var index = WeekdayIndex(date.DayOfWeek);
            weekdayScheduled[index]++;
            dailyScheduled[date.DayNumber - dateFrom.DayNumber]++;
            if (completedDates.Contains(date))
            {
                metCount++;
                weekdayCompleted[index]++;
                dailyCompleted[date.DayNumber - dateFrom.DayNumber]++;
            }
        }

        return metCount;
    }

    private static CompletionSeries BuildCompletionSeries(
        DateOnly dateFrom, DateOnly dateTo, int weekStartDay, int[] scheduled, int[] completed)
    {
        var isDaily = scheduled.Length <= 31;
        var points = new List<CompletionSeriesPoint>();
        var start = dateFrom;
        while (start <= dateTo)
        {
            var end = isDaily
                ? start
                : MinDate(start.AddDays(6 - ((7 + (int)start.DayOfWeek - weekStartDay) % 7)), dateTo);
            var bucketScheduled = 0;
            var bucketCompleted = 0;
            for (var date = start; date <= end; date = date.AddDays(1))
            {
                bucketScheduled += scheduled[date.DayNumber - dateFrom.DayNumber];
                bucketCompleted += completed[date.DayNumber - dateFrom.DayNumber];
            }

            points.Add(new CompletionSeriesPoint(
                start, end, bucketScheduled, bucketCompleted,
                bucketScheduled == 0 ? null : Math.Min(100, Percent(bucketCompleted, bucketScheduled))));
            start = end.AddDays(1);
        }

        return new CompletionSeries(isDaily ? "day" : "week", points);
    }

    private static DateOnly MinDate(DateOnly first, DateOnly second) => first < second ? first : second;

    private static RetrospectiveHabitStat BuildHabitStat(
        Habit habit, int scheduledCount, int completedCount, int metCount) =>
        new(
            habit.Title,
            habit.Emoji,
            Percent(metCount, scheduledCount),
            completedCount,
            scheduledCount,
            habit.FrequencyUnit is null);

    private static int[] BuildWeeklyConsistency(int[] weekdayScheduled, int[] weekdayCompleted)
    {
        var consistency = new int[7];
        for (var i = 0; i < 7; i++)
            consistency[i] = Math.Min(100, Percent(weekdayCompleted[i], weekdayScheduled[i]));
        return consistency;
    }

    private static int CountActiveDays(List<Habit> habits, DateOnly dateFrom, DateOnly dateTo)
    {
        var activeDates = new HashSet<DateOnly>();
        foreach (var habit in habits)
        {
            foreach (var log in habit.Logs)
            {
                if (!log.IsDeleted && log.Value > 0 && log.Date >= dateFrom && log.Date <= dateTo)
                    activeDates.Add(log.Date);
            }
        }
        return activeDates.Count;
    }

    private static int WeekdayIndex(DayOfWeek day) => Array.IndexOf(WeekOrder, day);

    private static int Percent(int numerator, int denominator) =>
        denominator > 0 ? (int)Math.Round(100.0 * numerator / denominator) : 0;
}
