using Orbit.Domain.Entities;

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
}
