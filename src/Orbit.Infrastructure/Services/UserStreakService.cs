using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

public class UserStreakService(
    IGenericRepository<User> userRepository,
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<StreakFreeze> streakFreezeRepository,
    IUserDateService userDateService) : IUserStreakService
{
    public async Task<UserStreakState?> RecalculateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == userId,
            cancellationToken: cancellationToken);
        if (user is null)
            return null;

        var userToday = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        var lookbackStart = userToday.AddDays(-AppConstants.MaxStreakLookbackDays);

        var (completionDateSet, freezeDateSet, contributingHabits) =
            await LoadStreakDataAsync(userId, lookbackStart, cancellationToken);

        // If the user has no recurring (non-bad, non-flexible, non-general) habits at all,
        // fall back to calendar-day adjacency so brand-new users aren't penalized.
        var hasRecurring = contributingHabits.Any(h => h.FrequencyUnit is not null);
        if (!hasRecurring)
        {
            return CalendarFallback(user, completionDateSet, freezeDateSet);
        }

        // Build expected-date timeline using historical schedule (anchored on CreatedAtUtc,
        // not DueDate) so that past dates remain visible after DueDate advances.
        var userTimeZone = user.TimeZone is not null
            ? TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone)
            : TimeZoneInfo.Utc;
        var expectedDates = HabitScheduleService.GetUnionScheduledDatesForStreak(
            contributingHabits, lookbackStart, userToday, userTimeZone);

        var currentStreak = ComputeCurrentStreak(
            expectedDates, completionDateSet, freezeDateSet, userToday, lookbackStart, out var lastActiveDate);

        // Longest streak: walk the full expected timeline forward and track the longest run.
        var longestStreak = ComputeLongestStreak(expectedDates, completionDateSet, freezeDateSet);
        if (currentStreak > longestStreak) longestStreak = currentStreak;

        user.SetStreakState(currentStreak, longestStreak, lastActiveDate);
        return new UserStreakState(currentStreak, longestStreak, lastActiveDate);
    }

    private async Task<(HashSet<DateOnly> CompletionDates, HashSet<DateOnly> FreezeDates, List<Habit> ContributingHabits)>
        LoadStreakDataAsync(Guid userId, DateOnly lookbackStart, CancellationToken cancellationToken)
    {
        var allHabits = await habitRepository.FindAsync(h => h.UserId == userId, cancellationToken);
        var allHabitIds = allHabits.Select(h => h.Id).ToHashSet();

        var completionDateSet = allHabitIds.Count == 0
            ? new HashSet<DateOnly>()
            : (await habitLogRepository.FindAsync(
                l => allHabitIds.Contains(l.HabitId) && l.Value > 0 && l.Date >= lookbackStart,
                cancellationToken))
                .Select(log => log.Date)
                .ToHashSet();

        var freezeDateSet = (await streakFreezeRepository.FindAsync(
            sf => sf.UserId == userId && sf.UsedOnDate >= lookbackStart,
            cancellationToken))
            .Select(freeze => freeze.UsedOnDate)
            .ToHashSet();

        var contributingHabits = allHabits
            .Where(h => !h.IsDeleted && !h.IsBadHabit && !h.IsGeneral && !h.IsFlexible)
            .Where(h => !(h.FrequencyUnit is null && h.IsCompleted))
            .ToList();

        return (completionDateSet, freezeDateSet, contributingHabits);
    }

    private static int ComputeCurrentStreak(
        HashSet<DateOnly> expectedDates,
        HashSet<DateOnly> completionDateSet,
        HashSet<DateOnly> freezeDateSet,
        DateOnly userToday,
        DateOnly lookbackStart,
        out DateOnly? lastActiveDate)
    {
        var currentStreak = 0;
        lastActiveDate = null;

        var cursor = userToday;
        if (!completionDateSet.Contains(cursor))
            cursor = cursor.AddDays(-1);

        while (cursor >= lookbackStart)
        {
            if (!expectedDates.Contains(cursor))
            {
                cursor = cursor.AddDays(-1);
                continue;
            }

            if (completionDateSet.Contains(cursor))
            {
                currentStreak++;
                lastActiveDate ??= cursor;
                cursor = cursor.AddDays(-1);
                continue;
            }

            if (freezeDateSet.Contains(cursor))
            {
                lastActiveDate ??= cursor;
                cursor = cursor.AddDays(-1);
                continue;
            }

            break;
        }

        return currentStreak;
    }

    private static int ComputeLongestStreak(
        HashSet<DateOnly> expectedDates,
        HashSet<DateOnly> completionDateSet,
        HashSet<DateOnly> freezeDateSet)
    {
        if (expectedDates.Count == 0) return 0;

        var ordered = expectedDates.OrderBy(d => d).ToList();
        var longest = 0;
        var run = 0;
        foreach (var date in ordered)
        {
            if (completionDateSet.Contains(date))
            {
                run++;
                if (run > longest) longest = run;
            }
            else if (!freezeDateSet.Contains(date))
            {
                run = 0;
            }
            // Freeze dates preserve the run without incrementing (no action needed).
        }
        return longest;
    }

    private static UserStreakState CalendarFallback(
        User user,
        HashSet<DateOnly> completionDateSet,
        HashSet<DateOnly> freezeDateSet)
    {
        var orderedDates = completionDateSet
            .Concat(freezeDateSet)
            .Distinct()
            .OrderBy(date => date)
            .ToList();

        var currentStreak = 0;
        var longestStreak = 0;
        DateOnly? lastActiveDate = null;

        foreach (var date in orderedDates)
        {
            if (completionDateSet.Contains(date))
            {
                currentStreak = lastActiveDate == date.AddDays(-1)
                    ? currentStreak + 1
                    : 1;
                lastActiveDate = date;
                if (currentStreak > longestStreak) longestStreak = currentStreak;
                continue;
            }

            if (!freezeDateSet.Contains(date))
                continue;

            if (lastActiveDate.HasValue
                && (date.DayNumber - lastActiveDate.Value.DayNumber) <= 2)
            {
                lastActiveDate = date;
            }
            else
            {
                currentStreak = 0;
                lastActiveDate = date;
            }
        }

        user.SetStreakState(currentStreak, longestStreak, lastActiveDate);
        return new UserStreakState(currentStreak, longestStreak, lastActiveDate);
    }
}
