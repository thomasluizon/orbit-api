using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Gamification.Backfill;

public sealed record AchievementEligibilityReconciliationResult(
    int AccountsGranted,
    int AchievementsGranted,
    int AccountsDeferred = 0);

public interface IAchievementEligibilityReconciliationService
{
    Task<AchievementEligibilityReconciliationResult> ReconcileAllAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reconciles active one-time achievements that existing users could satisfy while achievement earning
/// was unavailable to free accounts. Eligibility is derived from persisted state, and only missing
/// achievement ids are sent through <see cref="IGamificationService.ReconcileAchievementEligibilityAsync"/>.
/// </summary>
public sealed class AchievementEligibilityReconciliationService(
    IGenericRepository<User> userRepository,
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<Goal> goalRepository,
    IGenericRepository<UserAchievement> achievementRepository,
    IGamificationService gamificationService,
    IFeatureFlagService featureFlagService) : IAchievementEligibilityReconciliationService
{
    private static readonly string[] ReconciledAchievementIds =
    [
        AchievementDefinitions.Liftoff,
        AchievementDefinitions.FirstOrbit,
        AchievementDefinitions.MissionControl,
        AchievementDefinitions.GoalCrusher,
        AchievementDefinitions.OnboardingComplete
    ];

    public async Task<AchievementEligibilityReconciliationResult> ReconcileAllAsync(
        CancellationToken cancellationToken = default)
    {
        var candidates = await userRepository.FindTrackedAsync(
            user => user.AchievementEligibilityReconciledAtUtc == null,
            cancellationToken);
        if (candidates.Count == 0)
            return new AchievementEligibilityReconciliationResult(0, 0);

        var freeUsers = candidates.Where(user => !user.HasProAccess).ToList();
        var unlockedFreeUserIds = await featureFlagService.GetUserIdsWithEnabledKeyAsync(
            Common.FeatureFlagKeys.GamificationFreeTier,
            freeUsers,
            cancellationToken);
        var users = candidates
            .Where(user => user.HasProAccess || unlockedFreeUserIds.Contains(user.Id))
            .ToList();
        var accountsDeferred = freeUsers.Count(user => !unlockedFreeUserIds.Contains(user.Id));

        if (users.Count == 0)
            return new AchievementEligibilityReconciliationResult(0, 0, accountsDeferred);

        var userIds = users.Select(user => user.Id).ToList();
        var habits = await habitRepository.FindTrackedIgnoringFiltersAsync(
            habit => userIds.Contains(habit.UserId),
            cancellationToken);
        var goals = await goalRepository.FindTrackedIgnoringFiltersAsync(
            goal => userIds.Contains(goal.UserId),
            cancellationToken);
        var earnedAchievements = await achievementRepository.FindAsync(
            achievement => userIds.Contains(achievement.UserId)
                && ReconciledAchievementIds.Contains(achievement.AchievementId),
            cancellationToken);

        var completionHabitOwners = habits
            .Where(habit => !habit.IsBadHabit)
            .ToDictionary(habit => habit.Id, habit => habit.UserId);
        var completionHabitIds = completionHabitOwners.Keys.ToList();
        var completedHabitOwnerIds = new HashSet<Guid>();

        if (completionHabitIds.Count > 0)
        {
            var positiveLogs = await habitLogRepository.FindTrackedIgnoringFiltersAsync(
                log => completionHabitIds.Contains(log.HabitId) && log.Value > 0,
                cancellationToken);
            completedHabitOwnerIds = positiveLogs
                .Select(log => completionHabitOwners[log.HabitId])
                .ToHashSet();
        }

        var topLevelHabitOwnerIds = habits
            .Where(habit => habit.ParentHabitId is null)
            .Select(habit => habit.UserId)
            .ToHashSet();
        var goalOwnerIds = goals.Select(goal => goal.UserId).ToHashSet();
        var completedGoalOwnerIds = goals
            .Where(goal => goal.Status == GoalStatus.Completed)
            .Select(goal => goal.UserId)
            .ToHashSet();
        var earnedByUser = earnedAchievements
            .GroupBy(achievement => achievement.UserId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(achievement => achievement.AchievementId).ToHashSet());

        var accountsGranted = 0;
        var achievementsGranted = 0;

        foreach (var user in users)
        {
            earnedByUser.TryGetValue(user.Id, out var earned);
            earned ??= [];

            var eligible = BuildEligibleAchievementIds(
                user,
                topLevelHabitOwnerIds.Contains(user.Id),
                completedHabitOwnerIds.Contains(user.Id),
                goalOwnerIds.Contains(user.Id),
                completedGoalOwnerIds.Contains(user.Id));
            var missing = eligible.Where(id => !earned.Contains(id)).ToList();

            var granted = await gamificationService.ReconcileAchievementEligibilityAsync(
                user.Id,
                missing,
                cancellationToken);

            if (granted.Count > 0)
            {
                accountsGranted++;
                achievementsGranted += granted.Count;
            }
        }

        return new AchievementEligibilityReconciliationResult(
            accountsGranted,
            achievementsGranted,
            accountsDeferred);
    }

    private static IReadOnlyList<string> BuildEligibleAchievementIds(
        User user,
        bool hasTopLevelHabit,
        bool hasCompletedHabit,
        bool hasGoal,
        bool hasCompletedGoal)
    {
        var eligible = new List<string>(ReconciledAchievementIds.Length);

        if (hasCompletedHabit)
            eligible.Add(AchievementDefinitions.Liftoff);
        if (hasTopLevelHabit)
            eligible.Add(AchievementDefinitions.FirstOrbit);
        if (hasGoal)
            eligible.Add(AchievementDefinitions.MissionControl);
        if (hasCompletedGoal)
            eligible.Add(AchievementDefinitions.GoalCrusher);
        if (user.HasCompletedOnboardingChecklist)
            eligible.Add(AchievementDefinitions.OnboardingComplete);

        return eligible;
    }
}
