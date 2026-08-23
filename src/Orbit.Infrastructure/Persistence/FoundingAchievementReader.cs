using Microsoft.EntityFrameworkCore;
using Orbit.Application.Gamification;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public sealed class FoundingAchievementReader(OrbitDbContext context) : IFoundingAchievementReader
{
    public Task<FoundingAchievementEvidence?> ReadEvidenceAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var habits = context.Habits.IgnoreQueryFilters();
        var habitLogs = context.HabitLogs.IgnoreQueryFilters();
        var goals = context.Goals.IgnoreQueryFilters();

        return context.Users
            .AsNoTracking()
            .Where(user => user.Id == userId && !user.IsDeactivated)
            .Select(user => new FoundingAchievementEvidence(
                habits.Any(habit => habit.UserId == user.Id
                    && !habit.IsBadHabit
                    && habitLogs.Any(log => log.HabitId == habit.Id && log.Value > 0)),
                habits.Any(habit => habit.UserId == user.Id && habit.ParentHabitId == null),
                goals.Any(goal => goal.UserId == user.Id),
                context.XpAwardLogs.Any(award => award.UserId == user.Id
                    && award.Source == XpAwardSource.GoalCompleted)
                    || goals.Any(goal => goal.UserId == user.Id
                        && (goal.Status == GoalStatus.Completed || goal.FirstCompletedAtUtc != null)),
                user.HasCompletedOnboardingChecklist))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<IReadOnlyList<FoundingAchievementCursor>> ReadCandidatePageAsync(
        FoundingAchievementCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
        => ReadPageAsync(BuildCandidatePageQuery(cursor, pageSize), cancellationToken);

    internal IQueryable<FoundingAchievementCursor> BuildCandidatePageQuery(
        FoundingAchievementCursor? cursor,
        int pageSize)
    {
        var habits = context.Habits.IgnoreQueryFilters();
        var habitLogs = context.HabitLogs.IgnoreQueryFilters();
        var goals = context.Goals.IgnoreQueryFilters();
        var achievements = context.UserAchievements;

        var query = context.Users
            .AsNoTracking()
            .Where(user => !user.IsDeactivated
                && (
                (!achievements.Any(achievement => achievement.UserId == user.Id
                    && achievement.AchievementId == AchievementDefinitions.Liftoff)
                    && habits.Any(habit => habit.UserId == user.Id
                        && !habit.IsBadHabit
                        && habitLogs.Any(log => log.HabitId == habit.Id && log.Value > 0)))
                || (!achievements.Any(achievement => achievement.UserId == user.Id
                    && achievement.AchievementId == AchievementDefinitions.FirstOrbit)
                    && habits.Any(habit => habit.UserId == user.Id && habit.ParentHabitId == null))
                || (!achievements.Any(achievement => achievement.UserId == user.Id
                    && achievement.AchievementId == AchievementDefinitions.MissionControl)
                    && goals.Any(goal => goal.UserId == user.Id))
                || (!achievements.Any(achievement => achievement.UserId == user.Id
                    && achievement.AchievementId == AchievementDefinitions.GoalCrusher)
                    && (context.XpAwardLogs.Any(award => award.UserId == user.Id
                            && award.Source == XpAwardSource.GoalCompleted)
                        || goals.Any(goal => goal.UserId == user.Id
                            && (goal.Status == GoalStatus.Completed || goal.FirstCompletedAtUtc != null))))
                || (!achievements.Any(achievement => achievement.UserId == user.Id
                    && achievement.AchievementId == AchievementDefinitions.OnboardingComplete)
                    && user.HasCompletedOnboardingChecklist)));

        if (cursor is not null)
        {
            query = query.Where(user => EF.Functions.GreaterThan(
                ValueTuple.Create(user.Id, user.CreatedAtUtc),
                ValueTuple.Create(cursor.UserId, cursor.CreatedAtUtc)));
        }

        return query
            .OrderBy(user => user.Id)
            .ThenBy(user => user.CreatedAtUtc)
            .Select(user => new FoundingAchievementCursor(user.Id, user.CreatedAtUtc))
            .Take(pageSize);
    }

    private static async Task<IReadOnlyList<FoundingAchievementCursor>> ReadPageAsync(
        IQueryable<FoundingAchievementCursor> query,
        CancellationToken cancellationToken)
    {
        return await query.ToListAsync(cancellationToken);
    }
}
