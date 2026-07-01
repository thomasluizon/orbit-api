using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Challenges.Services;

/// <summary>
/// Pure read-side shared-progress math for challenges, mirroring <c>HabitMetricsCalculator</c>: a log
/// counts only when its <c>Value</c> is greater than 0 (0 is a skip). CoopGoal progress is the count of
/// qualifying logs across all contributing habits in the window; StreakTogether is the run of consecutive
/// days on which every contributing participant logged, lenient about an unfinished today, reset by any
/// single miss. A contributing participant is an active participant who has linked at least one habit.
/// </summary>
public static class ChallengeProgressCalculator
{
    public static int CalculateCoopGoalProgress(
        IReadOnlyCollection<Guid> contributingHabitIds,
        IReadOnlyCollection<HabitLog> logs,
        DateOnly periodStart,
        DateOnly periodEnd)
    {
        if (contributingHabitIds.Count == 0)
            return 0;

        return logs.Count(log =>
            log.Value > 0
            && contributingHabitIds.Contains(log.HabitId)
            && log.Date >= periodStart
            && log.Date <= periodEnd);
    }

    public static int CalculateSharedStreak(
        IReadOnlyList<IReadOnlyCollection<Guid>> contributingParticipantHabitSets,
        IReadOnlyCollection<HabitLog> logs,
        DateOnly periodStart,
        DateOnly lastDay,
        DateOnly today)
    {
        if (contributingParticipantHabitSets.Count == 0)
            return 0;

        var loggedDatesByParticipant = contributingParticipantHabitSets
            .Select(habitIds => logs
                .Where(log => log.Value > 0 && habitIds.Contains(log.HabitId))
                .Select(log => log.Date)
                .ToHashSet())
            .ToList();

        var streak = 0;
        for (var day = lastDay; day >= periodStart; day = day.AddDays(-1))
        {
            var everyoneLogged = loggedDatesByParticipant.All(dates => dates.Contains(day));

            if (day == today && !everyoneLogged && streak == 0)
                continue;

            if (!everyoneLogged)
                break;

            streak++;
        }

        return streak;
    }

    internal static IReadOnlyList<Guid> GetContributingHabitIds(Challenge challenge) =>
        GetContributingHabitSets(challenge).SelectMany(set => set).Distinct().ToList();

    internal static (int CurrentProgress, bool IsComplete) ComputeProgress(
        Challenge challenge,
        IReadOnlyCollection<HabitLog> logs,
        DateOnly lastDay,
        DateOnly today)
    {
        var contributingHabitSets = GetContributingHabitSets(challenge);

        if (challenge.Type == ChallengeType.CoopGoal)
        {
            var contributingHabitIds = contributingHabitSets.SelectMany(set => set).Distinct().ToList();
            var count = CalculateCoopGoalProgress(contributingHabitIds, logs, challenge.PeriodStartUtc, lastDay);
            var reachedTarget = challenge.TargetCount.HasValue && count >= challenge.TargetCount.Value;
            var windowEnded = challenge.PeriodEndUtc.HasValue && today > challenge.PeriodEndUtc.Value;
            return (count, challenge.Status == ChallengeStatus.Completed || reachedTarget || windowEnded);
        }

        var streak = CalculateSharedStreak(contributingHabitSets, logs, challenge.PeriodStartUtc, lastDay, today);
        return (streak, false);
    }

    private static List<IReadOnlyCollection<Guid>> GetContributingHabitSets(Challenge challenge) =>
        challenge.GetActiveParticipants()
            .Select(p => (IReadOnlyCollection<Guid>)p.LinkedHabits.Select(h => h.HabitId).ToList())
            .Where(set => set.Count > 0)
            .ToList();
}
