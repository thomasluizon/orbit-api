using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Gamification.Backfill;

/// <summary>
/// One-time, idempotent backfill that reconstructs each user's historical <see cref="XpAwardLog"/>
/// rows by replaying the real award curve — habit XP (10 + the recomputed per-habit streak on each
/// completion's date), goal-completion XP (+100 at completion), and achievement XP (+reward at unlock)
/// — then pins the cumulative tail to the user's stored <c>TotalXp</c> with a single
/// <see cref="XpAwardSource.Reconciliation"/> row for any drift. Skips any user that already has rows,
/// and only writes audit rows (never mutates <c>TotalXp</c>), so re-running is a no-op.
/// </summary>
public class XpAwardLogBackfillService(
    IGenericRepository<User> userRepository,
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<Goal> goalRepository,
    IGenericRepository<UserAchievement> achievementRepository,
    IGenericRepository<XpAwardLog> xpAwardLogRepository,
    IUnitOfWork unitOfWork)
{
    private const int HabitLogBaseXp = 10;
    private const int GoalCompletedXp = 100;

    /// <summary>
    /// Backfills every user. Returns the number of users for which rows were written (users that
    /// already had rows are skipped).
    /// </summary>
    public async Task<int> BackfillAllAsync(CancellationToken ct = default)
    {
        var users = await userRepository.GetAllAsync(ct);
        var processed = 0;
        foreach (var user in users)
        {
            if (await BackfillUserAsync(user.Id, ct))
                processed++;
        }

        return processed;
    }

    /// <summary>
    /// Replays and persists one user's XP audit rows. Returns false without writing anything when the
    /// user is unknown or already has <see cref="XpAwardLog"/> rows (idempotency guard).
    /// </summary>
    public async Task<bool> BackfillUserAsync(Guid userId, CancellationToken ct = default)
    {
        if (await xpAwardLogRepository.AnyAsync(x => x.UserId == userId, ct))
            return false;

        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return false;

        var rows = new List<XpAwardLog>();
        await AppendHabitLogRowsAsync(userId, user.TimeZone, rows, ct);
        await AppendGoalCompletionRowsAsync(userId, rows, ct);
        await AppendAchievementRowsAsync(userId, rows, ct);

        var replayedTotal = rows.Sum(r => r.Amount);
        var delta = user.TotalXp - replayedTotal;
        if (delta != 0)
        {
            var reconciledAtUtc = DateTime.UtcNow;
            rows.Add(XpAwardLog.Create(userId, delta, XpAwardSource.Reconciliation, sourceId: null, reconciledAtUtc));
        }

        foreach (var row in rows)
            await xpAwardLogRepository.AddAsync(row, ct);

        await unitOfWork.SaveChangesAsync(ct);
        return true;
    }

    private async Task AppendHabitLogRowsAsync(Guid userId, string? timeZone, List<XpAwardLog> rows, CancellationToken ct)
    {
        var habits = await habitRepository.FindAsync(
            h => h.UserId == userId && !h.IsBadHabit,
            q => q.Include(h => h.Logs),
            ct);

        var userTimeZone = TimeZoneHelper.FindTimeZone(timeZone);

        foreach (var habit in habits)
        {
            var completions = habit.Logs
                .Where(l => l.Value > 0)
                .OrderBy(l => l.Date)
                .ToList();

            foreach (var log in completions)
            {
                var streak = HabitMetricsCalculator.Calculate(habit, log.Date, userTimeZone).CurrentStreak;
                var awardedAtUtc = log.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                rows.Add(XpAwardLog.Create(userId, HabitLogBaseXp + streak, XpAwardSource.HabitLog, log.Id, awardedAtUtc));
            }
        }
    }

    private async Task AppendGoalCompletionRowsAsync(Guid userId, List<XpAwardLog> rows, CancellationToken ct)
    {
        var completedGoals = await goalRepository.FindAsync(
            g => g.UserId == userId && g.Status == GoalStatus.Completed && g.CompletedAtUtc != null,
            ct);

        foreach (var goal in completedGoals.OrderBy(g => g.CompletedAtUtc))
            rows.Add(XpAwardLog.Create(userId, GoalCompletedXp, XpAwardSource.GoalCompleted, goal.Id, goal.CompletedAtUtc!.Value));
    }

    private async Task AppendAchievementRowsAsync(Guid userId, List<XpAwardLog> rows, CancellationToken ct)
    {
        var achievements = await achievementRepository.FindAsync(a => a.UserId == userId, ct);

        foreach (var achievement in achievements.OrderBy(a => a.EarnedAtUtc))
        {
            var definition = AchievementDefinitions.GetById(achievement.AchievementId);
            if (definition is null)
                continue;

            rows.Add(XpAwardLog.Create(userId, definition.XpReward, XpAwardSource.Achievement, achievement.Id, achievement.EarnedAtUtc));
        }
    }
}
