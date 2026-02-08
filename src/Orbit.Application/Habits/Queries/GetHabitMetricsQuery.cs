using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Queries;

public record GetHabitMetricsQuery(Guid UserId, Guid HabitId) : IRequest<Result<HabitMetrics>>;

public class GetHabitMetricsQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<User> userRepository) : IRequestHandler<GetHabitMetricsQuery, Result<HabitMetrics>>
{
    public async Task<Result<HabitMetrics>> Handle(GetHabitMetricsQuery request, CancellationToken cancellationToken)
    {
        var habits = await habitRepository.FindAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId && h.IsActive,
            q => q.Include(h => h.Logs),
            cancellationToken);

        var habit = habits.FirstOrDefault();
        if (habit is null)
            return Result.Failure<HabitMetrics>("Habit not found.");

        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<HabitMetrics>("User not found.");

        var today = GetUserToday(user);
        var logDates = habit.Logs.Select(l => l.Date).Distinct().ToHashSet();
        var expectedDates = GenerateExpectedDates(habit, today).ToList();

        var currentStreak = CalculateCurrentStreak(habit, expectedDates, logDates, today);
        var longestStreak = CalculateLongestStreak(habit, expectedDates, logDates);
        var weeklyCompletionRate = CalculateCompletionRate(habit, expectedDates, logDates, today, 7);
        var monthlyCompletionRate = CalculateCompletionRate(habit, expectedDates, logDates, today, 30);
        var totalCompletions = logDates.Count;
        var lastCompletedDate = logDates.Count > 0 ? logDates.Max() : (DateOnly?)null;

        return Result.Success(new HabitMetrics(
            currentStreak,
            longestStreak,
            weeklyCompletionRate,
            monthlyCompletionRate,
            totalCompletions,
            lastCompletedDate));
    }

    private static DateOnly GetUserToday(User user)
    {
        var timeZone = user.TimeZone is not null
            ? TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone)
            : TimeZoneInfo.Utc;

        var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone);
        return DateOnly.FromDateTime(userNow);
    }

    private static IEnumerable<DateOnly> GenerateExpectedDates(Habit habit, DateOnly today)
    {
        var habitStartDate = DateOnly.FromDateTime(habit.CreatedAtUtc);
        var expectedDates = new List<DateOnly>();

        // One-time habit: only expected on the creation date
        if (habit.FrequencyUnit is null || habit.FrequencyQuantity is null)
        {
            expectedDates.Add(habitStartDate);
            return expectedDates;
        }

        if (habit.Days.Count > 0 && habit.FrequencyQuantity == 1)
        {
            // Day-of-week filtering mode
            var current = today;
            var iterations = 0;

            while (iterations < 365 && current >= habitStartDate)
            {
                if (habit.Days.Contains(current.DayOfWeek))
                {
                    expectedDates.Add(current);
                }

                current = current.AddDays(-1);
                iterations++;
            }
        }
        else
        {
            // Standard frequency mode
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

            if (habit.IsNegative)
            {
                // For negative habits, streak continues while date is NOT logged
                if (isLogged)
                    break;
                streak++;
            }
            else
            {
                // For positive habits, streak continues while date IS logged
                // Special case: if the most recent expected date is today and it hasn't been logged yet,
                // don't count it but don't break the streak either
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

            if (habit.IsNegative)
            {
                // For negative habits, count consecutive dates NOT logged
                if (isLogged)
                {
                    maxStreak = Math.Max(maxStreak, currentStreak);
                    currentStreak = 0;
                }
                else
                {
                    currentStreak++;
                }
            }
            else
            {
                // For positive habits, count consecutive dates logged
                if (!isLogged)
                {
                    maxStreak = Math.Max(maxStreak, currentStreak);
                    currentStreak = 0;
                }
                else
                {
                    currentStreak++;
                }
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

        var completedCount = habit.IsNegative
            ? expectedInRange.Count(d => !logDates.Contains(d))
            : expectedInRange.Count(d => logDates.Contains(d));

        return Math.Round((decimal)completedCount / expectedInRange.Count * 100, 2);
    }
}
