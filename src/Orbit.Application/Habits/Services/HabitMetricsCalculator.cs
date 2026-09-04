using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Services;

public static class HabitMetricsCalculator
{
    // Horizon must exceed the largest streak-achievement target (1000-day StreakImmortal) or those achievements can never hit 100%. https://github.com/thomasluizon/orbit-api/pull/419
    private const int MaxStreakHorizonDays = 1100;

    public static HabitMetrics Calculate(
        Habit habit,
        DateOnly today,
        int weekStartDay,
        TimeZoneInfo? userTimeZone = null)
    {
        return Calculate(habit, habit.Logs, today, weekStartDay, userTimeZone);
    }

    public static HabitMetrics Calculate(
        Habit habit,
        IReadOnlyCollection<HabitLog> logs,
        DateOnly today,
        int weekStartDay,
        TimeZoneInfo? userTimeZone = null)
    {
        var logDates = logs.Where(l => l.Value > 0).Select(l => l.Date).Distinct().ToHashSet();
        var habitStartDate = ResolveHabitStartDate(habit, logs, userTimeZone);
        var expectedDates = GenerateExpectedDates(habit, today, habitStartDate, weekStartDay).ToList();
        var streakCompletionDates = habit.IsFlexible
            ? GenerateCompletedFlexibleWindowDates(
                habit,
                logs,
                expectedDates,
                today,
                habitStartDate,
                weekStartDay)
            : logDates;

        var currentStreak = CalculateCurrentStreak(habit, expectedDates, streakCompletionDates, today);
        var longestStreak = CalculateLongestStreak(habit, expectedDates, streakCompletionDates);
        var weeklyCompletionRate = CalculateCompletionRate(habit, expectedDates, streakCompletionDates, today, 7);
        var monthlyCompletionRate = CalculateCompletionRate(habit, expectedDates, streakCompletionDates, today, 30);
        var totalCompletions = logDates.Count;
        var lastCompletedDate = logDates.Count > 0 ? logDates.Max() : (DateOnly?)null;

        return new HabitMetrics(
            currentStreak,
            longestStreak,
            weeklyCompletionRate,
            monthlyCompletionRate,
            totalCompletions,
            lastCompletedDate);
    }

    public static DateOnly GetUserToday(User user)
    {
        var timeZone = user.TimeZone is not null
            ? TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone)
            : TimeZoneInfo.Utc;

        var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone);
        return DateOnly.FromDateTime(userNow);
    }

    private static List<DateOnly> GenerateExpectedDates(
        Habit habit,
        DateOnly today,
        DateOnly habitStartDate,
        int weekStartDay)
    {
        if (habit.FrequencyUnit is null || habit.FrequencyQuantity is null)
            return [habitStartDate];

        if (habit.IsFlexible)
            return GenerateFlexibleWindowDates(habit, today, habitStartDate, weekStartDay);

        if (habit.IntervalWeeks is > 1)
            return GenerateIntervalExpectedDates(habit, today, habitStartDate, weekStartDay);

        if (habit.Days.Count > 0 && habit.FrequencyQuantity == 1)
            return GenerateDayFilteredDates(habit, today, habitStartDate);

        return GenerateFrequencyBasedDates(habit, today, habitStartDate);
    }

    private static List<DateOnly> GenerateIntervalExpectedDates(
        Habit habit,
        DateOnly today,
        DateOnly startDate,
        int weekStartDay)
    {
        var expectedDates = new List<DateOnly>();
        var current = today;
        var iterations = 0;

        while (iterations < MaxStreakHorizonDays && current >= startDate)
        {
            if (HabitScheduleService.IsHabitDueOnDateForStreakLookback(
                    habit,
                    current,
                    startDate,
                    weekStartDay))
            {
                expectedDates.Add(current);
            }

            current = current.AddDays(-1);
            iterations++;
        }

        return expectedDates;
    }

    private static List<DateOnly> GenerateFlexibleWindowDates(
        Habit habit,
        DateOnly today,
        DateOnly startDate,
        int weekStartDay)
    {
        var expectedDates = new List<DateOnly>();
        var horizonStart = today.AddDays(-(MaxStreakHorizonDays - 1));
        var firstDate = startDate > horizonStart ? startDate : horizonStart;
        var cursor = today;

        while (cursor >= firstDate)
        {
            var windowStart = HabitScheduleService.GetWindowStart(habit, cursor, weekStartDay);
            var windowEnd = HabitScheduleService.GetWindowEnd(habit, cursor, weekStartDay);
            if (windowEnd > today)
                windowEnd = today;

            var scanStart = windowStart > firstDate ? windowStart : firstDate;
            if (HasActiveIntervalDate(habit, scanStart, windowEnd, weekStartDay, startDate))
                expectedDates.Add(windowEnd);

            cursor = windowStart.AddDays(-1);
        }

        return expectedDates;
    }

    private static HashSet<DateOnly> GenerateCompletedFlexibleWindowDates(
        Habit habit,
        IReadOnlyCollection<HabitLog> logs,
        IReadOnlyCollection<DateOnly> windowMarkers,
        DateOnly today,
        DateOnly startDate,
        int weekStartDay)
    {
        var completedWindows = new HashSet<DateOnly>();
        var target = habit.FrequencyQuantity ?? 1;

        foreach (var marker in windowMarkers)
        {
            var start = HabitScheduleService.GetWindowStart(habit, marker, weekStartDay);
            if (start < startDate)
                start = startDate;
            var end = HabitScheduleService.GetWindowEnd(habit, marker, weekStartDay);
            if (end > today)
                end = today;
            var completed = logs.Count(log =>
                !log.IsDeleted
                && log.Value > 0
                && log.Date >= start
                && log.Date <= end
                && HabitScheduleService.IsActiveIntervalWeek(
                    habit,
                    log.Date,
                    weekStartDay,
                    startDate));
            if (completed >= target)
                completedWindows.Add(marker);
        }

        return completedWindows;
    }

    private static bool HasActiveIntervalDate(
        Habit habit,
        DateOnly start,
        DateOnly end,
        int weekStartDay,
        DateOnly recurrenceAnchor)
    {
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            if (HabitScheduleService.IsActiveIntervalWeek(habit, date, weekStartDay, recurrenceAnchor))
                return true;
        }

        return false;
    }

    private static DateOnly ResolveHabitStartDate(
        Habit habit,
        IReadOnlyCollection<HabitLog> logs,
        TimeZoneInfo? userTimeZone)
    {
        var tz = userTimeZone ?? TimeZoneInfo.Utc;
        var createdDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(habit.CreatedAtUtc, tz));
        return habit.ScheduledStartDate
            ?? ResolveLegacyStartDate(habit, logs, createdDate);
    }

    private static DateOnly ResolveLegacyStartDate(
        Habit habit,
        IReadOnlyCollection<HabitLog> logs,
        DateOnly createdDate)
    {
        var hasProgressingHistory = HasProgressingLegacyHistory(
            habit,
            logs,
            createdDate);
        return hasProgressingHistory ? createdDate : habit.DueDate;
    }

    private static bool HasProgressingLegacyHistory(
        Habit habit,
        IReadOnlyCollection<HabitLog> logs,
        DateOnly createdDate)
    {
        if (habit.FrequencyUnit is null || habit.IsBadHabit)
            return false;

        var resolvedDates = logs
            .Where(log => !log.IsDeleted)
            .Select(log => log.Date)
            .ToHashSet();
        var firstCandidate = createdDate < habit.DueDate.AddDays(-MaxStreakHorizonDays)
            ? habit.DueDate.AddDays(-MaxStreakHorizonDays)
            : createdDate;
        var candidateCount = habit.DueDate.DayNumber - firstCandidate.DayNumber;
        if (candidateCount <= 0)
            return false;

        var expectedBeforeDue = Enumerable.Range(0, candidateCount)
            .Select(firstCandidate.AddDays)
            .Where(date => HabitScheduleService.IsHabitDueOnDateForStreakLookback(habit, date, createdDate))
            .ToList();

        return expectedBeforeDue.Count > 0 && expectedBeforeDue.All(resolvedDates.Contains);
    }

    private static List<DateOnly> GenerateDayFilteredDates(Habit habit, DateOnly today, DateOnly startDate)
    {
        var expectedDates = new List<DateOnly>();
        var current = today;
        var iterations = 0;

        while (iterations < MaxStreakHorizonDays && current >= startDate)
        {
            if (habit.Days.Contains(current.DayOfWeek))
                expectedDates.Add(current);

            current = current.AddDays(-1);
            iterations++;
        }

        return expectedDates;
    }

    private static List<DateOnly> GenerateFrequencyBasedDates(Habit habit, DateOnly today, DateOnly startDate)
    {
        var expectedDates = new List<DateOnly>();
        var current = today;
        var iterations = 0;

        while (iterations < MaxStreakHorizonDays && current >= startDate)
        {
            expectedDates.Add(current);

            current = habit.FrequencyUnit switch
            {
                FrequencyUnit.Day => current.AddDays(-habit.FrequencyQuantity!.Value),
                FrequencyUnit.Week => current.AddDays(-7 * habit.FrequencyQuantity!.Value),
                FrequencyUnit.Month => current.AddMonths(-habit.FrequencyQuantity!.Value),
                FrequencyUnit.Year => current.AddYears(-habit.FrequencyQuantity!.Value),
                _ => throw new InvalidOperationException($"Unknown frequency unit: {habit.FrequencyUnit}")
            };

            iterations++;
        }

        return expectedDates;
    }

    private static int CalculateCurrentStreak(
        Habit habit,
        List<DateOnly> expectedDates,
        HashSet<DateOnly> logDates,
        DateOnly today)
    {
        if (expectedDates.Count == 0)
            return 0;

        var streak = 0;

        foreach (var date in expectedDates)
        {
            var isLogged = logDates.Contains(date);

            if (habit.IsBadHabit)
            {
                if (isLogged)
                    break;
                streak++;
            }
            else
            {
                if (date == today && !isLogged && streak == 0)
                    continue;

                if (!isLogged)
                    break;
                streak++;
            }
        }

        return streak;
    }

    private static int CalculateLongestStreak(
        Habit habit,
        List<DateOnly> expectedDates,
        HashSet<DateOnly> logDates)
    {
        if (expectedDates.Count == 0)
            return 0;

        var maxStreak = 0;
        var currentStreak = 0;

        foreach (var date in expectedDates)
        {
            var isLogged = logDates.Contains(date);
            var breaksStreak = habit.IsBadHabit ? isLogged : !isLogged;

            if (breaksStreak)
            {
                maxStreak = Math.Max(maxStreak, currentStreak);
                currentStreak = 0;
            }
            else
            {
                currentStreak++;
            }
        }

        return Math.Max(maxStreak, currentStreak);
    }

    private static decimal CalculateCompletionRate(
        Habit habit,
        List<DateOnly> allExpectedDates,
        HashSet<DateOnly> logDates,
        DateOnly today,
        int daysBack)
    {
        var startDate = today.AddDays(-daysBack);
        var expectedInRange = allExpectedDates
            .Where(d => d >= startDate && d <= today)
            .ToList();

        if (expectedInRange.Count == 0)
            return 0;

        var completedCount = habit.IsBadHabit
            ? expectedInRange.Count(d => !logDates.Contains(d))
            : expectedInRange.Count(d => logDates.Contains(d));

        return Math.Round((decimal)completedCount / expectedInRange.Count * 100, 2);
    }
}
