using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

public partial class UserStreakService(
    IGenericRepository<User> userRepository,
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<StreakFreeze> streakFreezeRepository,
    IUserDateService userDateService,
    IFriendFeedEventEmitter friendFeedEventEmitter,
    IUnitOfWork unitOfWork,
    IFeatureFlagService featureFlagService,
    ILogger<UserStreakService> logger) : IUserStreakService
{
    public async Task<UserStreakState?> RecalculateAsync(
        Guid userId,
        CancellationToken cancellationToken = default,
        bool awardFreezeIfEligible = true)
    {
        var user = await userRepository.FindOneTrackedAsync(
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

        if (await TryBridgeRecentGapWithBankedFreezeAsync(
                user, userToday, lookbackStart, expectedDates, completionDateSet, freezeDateSet,
                currentStreak, cancellationToken))
        {
            (currentStreak, lastActiveDate) = HabitScheduleService.ComputeStreakAsOf(
                expectedDates, completionDateSet, freezeDateSet, lookbackStart, userToday);
        }

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

    private async Task<(HashSet<DateOnly> CompletionDates, HashSet<DateOnly> FreezeDates, List<Habit> ContributingHabits)>
        LoadStreakDataAsync(Guid userId, DateOnly lookbackStart, CancellationToken cancellationToken)
    {
        var allHabits = await habitRepository.FindAsync(h => h.UserId == userId, cancellationToken);
        var streakEligibleHabitIds = allHabits
            .Where(h => !h.IsDeleted && !h.IsBadHabit)
            .Select(h => h.Id)
            .ToHashSet();

        var completionDateSet = streakEligibleHabitIds.Count == 0
            ? new HashSet<DateOnly>()
            : (await habitLogRepository.FindAsync(
                l => streakEligibleHabitIds.Contains(l.HabitId) && l.Value > 0 && l.Date >= lookbackStart,
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

    /// <summary>
    /// Applies one banked streak freeze to bridge the user's most recent scheduled miss (their local
    /// "yesterday") during recalculation — the same action the hourly <see cref="StreakFreezeAutoActivationService"/>
    /// takes — so the streak is preserved regardless of which path runs first. The consume + the
    /// <see cref="StreakFreeze"/> insert are flushed in one guarded save so they commit atomically; a persisted row
    /// makes the operation idempotent, since a later recalculation sees the frozen date in
    /// <paramref name="freezeDateSet"/> and neither re-consumes a freeze nor inflates the streak. The hourly job
    /// can insert the same <c>(UserId, UsedOnDate)</c> row concurrently: on that unique-violation the save rolls
    /// back this consume (so exactly one freeze is spent overall), the staged rows are dropped, and the day is
    /// still treated as covered because the winner's row already bridges it. Only spends a freeze when covering the
    /// day actually raises the streak (so an over-large gap is left to break) and the user is freeze-eligible and
    /// under the monthly cap. Returns true, extending <paramref name="freezeDateSet"/> with the covered date,
    /// whenever the day ends up covered by this call or its concurrent winner.
    /// </summary>
    private async Task<bool> TryBridgeRecentGapWithBankedFreezeAsync(
        User user,
        DateOnly userToday,
        DateOnly lookbackStart,
        HashSet<DateOnly> expectedDates,
        HashSet<DateOnly> completionDateSet,
        HashSet<DateOnly> freezeDateSet,
        int currentStreak,
        CancellationToken cancellationToken)
    {
        if (user.StreakFreezesAccumulated <= 0)
            return false;

        var missedDate = userToday.AddDays(-1);
        if (!expectedDates.Contains(missedDate)
            || completionDateSet.Contains(missedDate)
            || freezeDateSet.Contains(missedDate))
        {
            return false;
        }

        var monthStart = new DateOnly(missedDate.Year, missedDate.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var freezesThisMonth = freezeDateSet.Count(date => date >= monthStart && date < monthEnd);
        if (freezesThisMonth >= AppConstants.MaxStreakFreezesPerMonth)
            return false;

        var bridged = new HashSet<DateOnly>(freezeDateSet) { missedDate };
        var (streakWithBridge, _) = HabitScheduleService.ComputeStreakAsOf(
            expectedDates, completionDateSet, bridged, lookbackStart, userToday);
        if (streakWithBridge <= currentStreak)
            return false;

        var enabledFlags = await featureFlagService.GetEnabledKeysForUserAsync(user.Id, cancellationToken);
        if (!user.HasProAccess && !enabledFlags.Contains(FeatureFlagKeys.GamificationFreeTier))
            return false;

        if (user.ConsumeStreakFreeze().IsFailure)
            return false;

        await streakFreezeRepository.AddAsync(StreakFreeze.Create(user.Id, missedDate), cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            unitOfWork.DiscardChanges();
            freezeDateSet.Add(missedDate);
            if (logger.IsEnabled(LogLevel.Debug))
                LogBankedFreezeAlreadyCovered(logger, user.Id, missedDate);
            return true;
        }

        freezeDateSet.Add(missedDate);
        if (logger.IsEnabled(LogLevel.Information))
            LogBankedFreezeApplied(logger, user.Id, missedDate);

        return true;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Applied banked streak freeze for user {UserId} on {FrozenDate} during recalculation to bridge a missed scheduled day")]
    private static partial void LogBankedFreezeApplied(ILogger logger, Guid userId, DateOnly frozenDate);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Banked streak freeze for user {UserId} on {FrozenDate} was already inserted by a concurrent activation; treating the day as covered")]
    private static partial void LogBankedFreezeAlreadyCovered(ILogger logger, Guid userId, DateOnly frozenDate);

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
                currentStreak = lastActiveDate == date.AddDays(-1)
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
