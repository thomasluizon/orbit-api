using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

/// <summary>Groups the repositories the streak service touches to keep its constructor small.</summary>
public record UserStreakRepositories(
    IGenericRepository<User> Users,
    IGenericRepository<Habit> Habits,
    IGenericRepository<HabitLog> HabitLogs,
    IGenericRepository<StreakFreeze> StreakFreezes);

public class UserStreakService(
    UserStreakRepositories repos,
    IUserDateService userDateService,
    IFriendFeedEventEmitter friendFeedEventEmitter) : IUserStreakService
{
    public async Task<UserStreakState?> RecalculateAsync(
        Guid userId,
        bool awardFreezeIfEligible = true,
        CancellationToken cancellationToken = default)
    {
        var user = await repos.Users.FindOneTrackedAsync(
            u => u.Id == userId,
            cancellationToken: cancellationToken);
        if (user is null)
            return null;

        var previousStreak = user.CurrentStreak;
        var userToday = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        var lookbackStart = userToday.AddDays(-AppConstants.MaxStreakLookbackDays);

        var (completionDateSet, freezeDateSet, contributingHabits) =
            await LoadStreakDataAsync(userId, lookbackStart, cancellationToken);

        var hasRecurring = contributingHabits.Any(h => h.FrequencyUnit is not null);
        if (!hasRecurring)
        {
            var fallbackState = CalendarFallback(user, completionDateSet, freezeDateSet, awardFreezeIfEligible);
            await friendFeedEventEmitter.EmitStreakMilestonesAsync(user, previousStreak, cancellationToken);
            return fallbackState;
        }

        var userTimeZone = TimeZoneHelper.FindTimeZone(user.TimeZone, userId: user.Id);
        var expectedDates = HabitScheduleService.GetUnionScheduledDatesForStreak(
            contributingHabits, lookbackStart, userToday, userTimeZone);

        var (currentStreak, lastActiveDate) = HabitScheduleService.ComputeStreakAsOf(
            expectedDates, completionDateSet, freezeDateSet, lookbackStart, userToday);

        var longestStreak = ComputeLongestStreak(expectedDates, completionDateSet, freezeDateSet);
        if (currentStreak > longestStreak) longestStreak = currentStreak;

        user.SetStreakState(currentStreak, longestStreak, lastActiveDate);
        if (awardFreezeIfEligible)
        {
            user.AwardStreakFreezeIfEligible(
                AppConstants.MaxStreakFreezesAccumulated,
                AppConstants.StreakDaysPerFreeze);
        }
        await friendFeedEventEmitter.EmitStreakMilestonesAsync(user, previousStreak, cancellationToken);
        return new UserStreakState(currentStreak, longestStreak, lastActiveDate);
    }

    public async Task<StreakRepairEvaluation?> EvaluateRepairAsync(
        Guid userId,
        DateOnly userToday,
        DateOnly missedDate,
        CancellationToken cancellationToken = default)
    {
        var user = await repos.Users.FindOneTrackedAsync(
            u => u.Id == userId,
            cancellationToken: cancellationToken);
        if (user is null)
            return null;

        if (missedDate.DayNumber != userToday.DayNumber - 1)
            return StreakRepairEvaluation.Unavailable(missedDate);

        var lookbackStart = userToday.AddDays(-AppConstants.MaxStreakLookbackDays);
        var (completionDateSet, freezeDateSet, contributingHabits) =
            await LoadStreakDataAsync(userId, lookbackStart, cancellationToken);

        if (user.StreakFreezesAccumulated <= 0
            || !contributingHabits.Any(habit => habit.FrequencyUnit is not null))
        {
            return StreakRepairEvaluation.Unavailable(missedDate);
        }

        var userTimeZone = TimeZoneHelper.FindTimeZone(user.TimeZone, userId: user.Id);
        var expectedDates = HabitScheduleService.GetUnionScheduledDatesForStreak(
            contributingHabits, lookbackStart, userToday, userTimeZone);

        if (!expectedDates.Contains(missedDate)
            || completionDateSet.Contains(missedDate)
            || freezeDateSet.Contains(missedDate))
        {
            return StreakRepairEvaluation.Unavailable(missedDate);
        }

        var monthStart = new DateOnly(missedDate.Year, missedDate.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var freezesThisMonth = freezeDateSet.Count(date => date >= monthStart && date < monthEnd);
        if (freezesThisMonth >= AppConstants.MaxStreakFreezesPerMonth)
            return StreakRepairEvaluation.Unavailable(missedDate);

        var (currentStreak, _) = HabitScheduleService.ComputeStreakAsOf(
            expectedDates, completionDateSet, freezeDateSet, lookbackStart, userToday);
        var repairedFreezeDates = new HashSet<DateOnly>(freezeDateSet) { missedDate };
        var (repairedStreak, repairedLastActiveDate) = HabitScheduleService.ComputeStreakAsOf(
            expectedDates, completionDateSet, repairedFreezeDates, lookbackStart, userToday);
        if (repairedStreak <= currentStreak)
            return StreakRepairEvaluation.Unavailable(missedDate);

        var repairedLongestStreak = ComputeLongestStreak(
            expectedDates, completionDateSet, repairedFreezeDates);
        repairedLongestStreak = Math.Max(
            user.LongestStreak,
            Math.Max(repairedLongestStreak, repairedStreak));

        return StreakRepairEvaluation.Available(
            missedDate,
            new UserStreakState(repairedStreak, repairedLongestStreak, repairedLastActiveDate));
    }

    private async Task<(HashSet<DateOnly> CompletionDates, HashSet<DateOnly> FreezeDates, List<Habit> ContributingHabits)>
        LoadStreakDataAsync(Guid userId, DateOnly lookbackStart, CancellationToken cancellationToken)
    {
        var allHabits = await repos.Habits.FindAsync(h => h.UserId == userId, cancellationToken);
        var streakEligibleHabitIds = allHabits
            .Where(h => !h.IsDeleted && !h.IsBadHabit)
            .Select(h => h.Id)
            .ToHashSet();

        var completionDateSet = streakEligibleHabitIds.Count == 0
            ? new HashSet<DateOnly>()
            : (await repos.HabitLogs.FindAsync(
                l => streakEligibleHabitIds.Contains(l.HabitId) && l.Value > 0 && l.Date >= lookbackStart,
                cancellationToken))
                .Select(log => log.Date)
                .ToHashSet();

        var freezeDateSet = (await repos.StreakFreezes.FindAsync(
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
        }
        return longest;
    }

    private static UserStreakState CalendarFallback(
        User user,
        HashSet<DateOnly> completionDateSet,
        HashSet<DateOnly> freezeDateSet,
        bool awardFreezeIfEligible)
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
                currentStreak = lastActiveDate.HasValue
                    && lastActiveDate.Value.DayNumber == date.DayNumber - 1
                    ? currentStreak + 1
                    : 1;
                lastActiveDate = date;
                longestStreak = Math.Max(longestStreak, currentStreak);
                continue;
            }

            if (!freezeDateSet.Contains(date))
                continue;

            if (!lastActiveDate.HasValue
                || (date.DayNumber - lastActiveDate.Value.DayNumber) > 2)
            {
                currentStreak = 0;
            }
            lastActiveDate = date;
        }

        user.SetStreakState(currentStreak, longestStreak, lastActiveDate);
        if (awardFreezeIfEligible)
        {
            user.AwardStreakFreezeIfEligible(
                AppConstants.MaxStreakFreezesAccumulated,
                AppConstants.StreakDaysPerFreeze);
        }
        return new UserStreakState(currentStreak, longestStreak, lastActiveDate);
    }
}
