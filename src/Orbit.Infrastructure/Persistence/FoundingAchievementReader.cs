using Microsoft.EntityFrameworkCore;
using Orbit.Application.Gamification;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public sealed class FoundingAchievementReader(OrbitDbContext context) : IFoundingAchievementReader
{
    public async Task<FoundingAchievementEvidence?> ReadEvidenceAsync(Guid userId, CancellationToken cancellationToken = default) =>
        (await ReadEvidencePageAsync([userId], cancellationToken)).GetValueOrDefault(userId);

    public async Task<IReadOnlyDictionary<Guid, FoundingAchievementEvidence>> ReadEvidencePageAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        var habits = context.Habits.IgnoreQueryFilters();
        var habitLogs = context.HabitLogs.IgnoreQueryFilters();
        var goals = context.Goals.IgnoreQueryFilters();
        return await context.Users.AsNoTracking().Where(user => userIds.Contains(user.Id) && !user.IsDeactivated)
            .Select(user => new
            {
                user.Id,
                Evidence = new FoundingAchievementEvidence(
                habits.Any(habit => habit.UserId == user.Id
                    && !habit.IsBadHabit
                    && habitLogs.Any(log => log.HabitId == habit.Id && log.Value > 0)),
                habits.Any(habit => habit.UserId == user.Id && habit.ParentHabitId == null),
                goals.Any(goal => goal.UserId == user.Id),
                context.XpAwardLogs.Any(award => award.UserId == user.Id
                    && award.Source == XpAwardSource.GoalCompleted)
                    || goals.Any(goal => goal.UserId == user.Id
                        && (goal.Status == GoalStatus.Completed || goal.FirstCompletedAtUtc != null)),
                user.HasCompletedOnboardingChecklist)
            })
            .ToDictionaryAsync(row => row.Id, row => row.Evidence, cancellationToken);
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
