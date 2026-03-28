using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Services;

public static class HabitMetricsCalculator
{
    public static HabitMetrics Calculate(Habit habit, DateOnly today, TimeZoneInfo? userTimeZone = null)
    {
        var logDates = habit.Logs.Where(l => l.Value > 0).Select(l => l.Date).Distinct().ToHashSet();
        var expectedDates = GenerateExpectedDates(habit, today, userTimeZone).ToList();

        var currentStreak = CalculateCurrentStreak(habit, expectedDates, logDates, today);
        var longestStreak = CalculateLongestStreak(habit, expectedDates, logDates);
        var weeklyCompletionRate = CalculateCompletionRate(habit, expectedDates, logDates, today, 7);
        var monthlyCompletionRate = CalculateCompletionRate(habit, expectedDates, logDates, today, 30);
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

    private static IEnumerable<DateOnly> GenerateExpectedDates(Habit habit, DateOnly today, TimeZoneInfo? userTimeZone = null)
    {
        // Convert habit creation timestamp to user local date to avoid UTC-vs-local day boundary errors
        var tz = userTimeZone ?? TimeZoneInfo.Utc;
        var habitStartDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(habit.CreatedAtUtc, tz));
        var expectedDates = new List<DateOnly>();

        if (habit.FrequencyUnit is null || habit.FrequencyQuantity is null)
        {
            expectedDates.Add(habitStartDate);
            return expectedDates;
        }

        if (habit.Days.Count > 0 && habit.FrequencyQuantity == 1)
        {
            var current = today;
            var iterations = 0;

            while (iterations < 365 && current >= habitStartDate)
            {
                if (habit.Days.Contains(current.DayOfWeek))
                    expectedDates.Add(current);

                current = current.AddDays(-1);
                iterations++;
            }
        }
        else
        {
            var current = today;
            var iterations = 0;

            while (iterations < 365 && current >= habitStartDate)
            {
                expectedDates.Add(current);

                current = habit.FrequencyUnit switch
                {
                    FrequencyUnit.Day => current.AddDays(-habit.FrequencyQuantity.Value),
                    FrequencyUnit.Week => current.AddDays(-7 * habit.FrequencyQuantity.Value),
                    FrequencyUnit.Month => current.AddMonths(-habit.FrequencyQuantity.Value),
                    FrequencyUnit.Year => current.AddYears(-habit.FrequencyQuantity.Value),
                    _ => throw new InvalidOperationException($"Unknown frequency unit: {habit.FrequencyUnit}")
                };

                iterations++;
            }
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

        foreach (var date in expectedDates.OrderByDescending(d => d))
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

        foreach (var date in expectedDates.OrderByDescending(d => d))
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
