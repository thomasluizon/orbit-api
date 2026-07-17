using Orbit.Application.Common;
using Orbit.Application.Gamification.Models;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Gamification.Services;

public interface IAchievementProgressService
{
    /// <summary>
    /// Loads the user's current values for every quantifiable achievement metric in a fixed number of
    /// queries (independent of the achievement count). <paramref name="earnedIds"/> lets the expensive
    /// time-of-day log scan be skipped when both Early Bird and Night Owl are already earned.
    /// </summary>
    Task<AchievementProgressMetrics> LoadAsync(User user, IReadOnlySet<string> earnedIds, CancellationToken cancellationToken);
}

public class AchievementProgressService(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<Goal> goalRepository,
    IGenericRepository<Cheer> cheerRepository,
    FriendGraphService friendGraphService,
    IUserDateService userDateService) : IAchievementProgressService
{
    private const int TotalCompletionWindowDays = 400;
    private const int TimeOfDayWindowDays = 90;
    private const int EarlyBeforeHour = 7;
    private const int NightFromHour = 22;

    public async Task<AchievementProgressMetrics> LoadAsync(
        User user, IReadOnlySet<string> earnedIds, CancellationToken cancellationToken)
    {
        var today = await userDateService.GetUserTodayAsync(user.Id, cancellationToken);

        var habits = await habitRepository.FindAsync(h => h.UserId == user.Id, cancellationToken);
        var habitIds = habits.Select(h => h.Id).ToList();

        var totalCompletionCutoff = today.AddDays(-TotalCompletionWindowDays);
        var totalCompletions = habitIds.Count == 0
            ? 0
            : await habitLogRepository.CountAsync(
                l => habitIds.Contains(l.HabitId) && l.Date >= totalCompletionCutoff, cancellationToken);

        var goalsCreated = await goalRepository.CountAsync(g => g.UserId == user.Id, cancellationToken);
        var goalsCompleted = await goalRepository.CountAsync(
            g => g.UserId == user.Id && g.Status == GoalStatus.Completed, cancellationToken);
        var friendsCount = await friendGraphService.CountAcceptedFriendsAsync(user.Id, cancellationToken);
        var cheersSent = await cheerRepository.CountAsync(c => c.SenderId == user.Id, cancellationToken);

        var (earlyLogs, nightLogs) = await CountTimeOfDayLogsAsync(user, habitIds, earnedIds, cancellationToken);

        return new AchievementProgressMetrics(
            user.CurrentStreak,
            totalCompletions,
            goalsCreated,
            goalsCompleted,
            friendsCount,
            cheersSent,
            earlyLogs,
            nightLogs);
    }

    private async Task<(int Early, int Night)> CountTimeOfDayLogsAsync(
        User user, IReadOnlyList<Guid> habitIds, IReadOnlySet<string> earnedIds, CancellationToken cancellationToken)
    {
        if (habitIds.Count == 0)
            return (0, 0);
        if (earnedIds.Contains(AchievementDefinitions.EarlyBird) && earnedIds.Contains(AchievementDefinitions.NightOwl))
            return (0, 0);

        var createdAtUtcCutoff = DateTime.UtcNow.AddDays(-TimeOfDayWindowDays);
        var logs = await habitLogRepository.FindAsync(
            l => habitIds.Contains(l.HabitId) && l.CreatedAtUtc >= createdAtUtcCutoff, cancellationToken);

        var userTimeZone = TimeZoneHelper.FindTimeZone(user.TimeZone);
        var early = 0;
        var night = 0;
        foreach (var log in logs)
        {
            var localHour = TimeZoneInfo.ConvertTimeFromUtc(log.CreatedAtUtc, userTimeZone).Hour;
            if (localHour < EarlyBeforeHour)
                early++;
            else if (localHour >= NightFromHour)
                night++;
        }

        return (early, night);
    }
}
