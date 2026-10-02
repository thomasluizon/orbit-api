using Orbit.Application.Common;
using Orbit.Application.Gamification.Models;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Gamification.Services;

public interface IAchievementProgressService
{
    /// <summary>
    /// Loads the user's current values for every quantifiable achievement metric in a bounded number
    /// of queries independent of the achievement count, expanding the log window for long streaks.
    /// <paramref name="earnedIds"/> skips streak and time-of-day reads when their achievements are earned.
    /// </summary>
    Task<AchievementProgressMetrics> LoadAsync(User user, IReadOnlySet<string> earnedIds, CancellationToken cancellationToken);
}

public class AchievementProgressService(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<Goal> goalRepository,
    IUserDateService userDateService) : IAchievementProgressService
{
    private const int TotalCompletionWindowDays = 2750;
    private const int InitialStreakWindowDays = 64;
    private const int TimeOfDayWindowDays = 90;
    private const int EarlyBeforeHour = 7;
    private const int NightFromHour = 22;

    public async Task<AchievementProgressMetrics> LoadAsync(
        User user, IReadOnlySet<string> earnedIds, CancellationToken cancellationToken)
    {
        var today = await userDateService.GetUserTodayAsync(user.Id, cancellationToken);
        var userTimeZone = TimeZoneHelper.FindTimeZone(user.TimeZone);

        var habits = (await habitRepository.ProjectAsync(
            h => h.UserId == user.Id, HabitScheduleProjection.Select, cancellationToken))
            .Select(snapshot => Habit.FromScheduleSnapshot(snapshot, user.Id))
            .ToList();
        var habitIds = habits.Select(h => h.Id).ToList();
        var goodHabits = habits.Where(h => !h.IsBadHabit).ToList();
        var maxCurrentStreak = await LoadMaxCurrentStreakAsync(
            goodHabits, earnedIds, today, user.WeekStartDay, userTimeZone, cancellationToken);

        var totalCompletionCutoff = today.AddDays(-TotalCompletionWindowDays);
        var totalCompletions = habitIds.Count == 0
            ? 0
            : await habitLogRepository.CountAsync(
                l => habitIds.Contains(l.HabitId) && l.Date >= totalCompletionCutoff, cancellationToken);

        var goalsCreated = await goalRepository.CountAsync(g => g.UserId == user.Id, cancellationToken);
        var goalsCompleted = await goalRepository.CountAsync(
            g => g.UserId == user.Id && g.Status == GoalStatus.Completed, cancellationToken);

        var (earlyLogs, nightLogs) = await CountTimeOfDayLogsAsync(
            habitIds, earnedIds, userTimeZone, cancellationToken);

        return new AchievementProgressMetrics(
            maxCurrentStreak,
            totalCompletions,
            goalsCreated,
            goalsCompleted,
            earlyLogs,
            nightLogs);
    }

    private async Task<int> LoadMaxCurrentStreakAsync(
        IReadOnlyList<Habit> goodHabits, IReadOnlySet<string> earnedIds, DateOnly today,
        int weekStartDay, TimeZoneInfo userTimeZone, CancellationToken cancellationToken)
    {
        if (goodHabits.Count == 0 || AchievementDefinitions.All
            .Where(definition => definition.Metric == ProgressMetric.CurrentStreak)
            .All(definition => earnedIds.Contains(definition.Id)))
            return 0;

        var pendingHabits = goodHabits.ToList();
        var logsByHabit = goodHabits.ToDictionary(habit => habit.Id, _ => new List<HabitMetricLog>());
        var windowDays = InitialStreakWindowDays;
        DateOnly? previousCutoff = null;
        var maxCurrentStreak = 0;

        while (pendingHabits.Count > 0)
        {
            var cutoff = today.AddDays(-windowDays);
            var pendingIds = pendingHabits.Select(habit => habit.Id).ToList();
            var logs = await habitLogRepository.ProjectAsync(
                log => pendingIds.Contains(log.HabitId) && log.Date >= cutoff
                    && (previousCutoff == null || log.Date < previousCutoff),
                query => query.Select(log => new HabitMetricLog(log.HabitId, log.Date, log.Value, false)),
                cancellationToken);
            foreach (var log in logs)
                logsByHabit[log.HabitId].Add(log);

            var continuingHabits = new List<Habit>();
            foreach (var habit in pendingHabits)
            {
                var metrics = HabitMetricsCalculator.CalculateProjected(
                    habit, logsByHabit[habit.Id], today, weekStartDay, userTimeZone, out var logStart);
                if (windowDays < GamificationService.StreakLogWindowDays && logStart < cutoff)
                    continuingHabits.Add(habit);
                else
                    maxCurrentStreak = Math.Max(maxCurrentStreak, metrics.CurrentStreak);
            }

            pendingHabits = continuingHabits;
            previousCutoff = cutoff;
            windowDays = Math.Min(windowDays * 2, GamificationService.StreakLogWindowDays);
        }

        return maxCurrentStreak;
    }

    private async Task<(int Early, int Night)> CountTimeOfDayLogsAsync(
        IReadOnlyList<Guid> habitIds, IReadOnlySet<string> earnedIds,
        TimeZoneInfo userTimeZone, CancellationToken cancellationToken)
    {
        if (habitIds.Count == 0)
            return (0, 0);
        if (earnedIds.Contains(AchievementDefinitions.EarlyBird) && earnedIds.Contains(AchievementDefinitions.NightOwl))
            return (0, 0);

        var createdAtUtcCutoff = DateTime.UtcNow.AddDays(-TimeOfDayWindowDays);
        var logs = await habitLogRepository.ProjectAsync(
            l => habitIds.Contains(l.HabitId) && l.CreatedAtUtc >= createdAtUtcCutoff,
            query => query.Select(log => log.CreatedAtUtc), cancellationToken);

        var early = 0;
        var night = 0;
        foreach (var createdAtUtc in logs)
        {
            var localHour = TimeZoneInfo.ConvertTimeFromUtc(createdAtUtc, userTimeZone).Hour;
            if (localHour < EarlyBeforeHour)
                early++;
            else if (localHour >= NightFromHour)
                night++;
        }

        return (early, night);
    }
}
