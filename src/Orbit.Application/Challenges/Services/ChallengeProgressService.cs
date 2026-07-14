using Microsoft.EntityFrameworkCore;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Models;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Challenges.Services;

/// <summary>Groups the repositories the challenge progress seam touches to keep the constructor small.</summary>
public record ChallengeProgressRepositories(
    IGenericRepository<Challenge> Challenges,
    IGenericRepository<ChallengeParticipant> Participants,
    IGenericRepository<ChallengeParticipantHabit> ParticipantHabits,
    IGenericRepository<HabitLog> HabitLogs,
    IGenericRepository<User> Users,
    IGenericRepository<UserAchievement> Achievements);

public class ChallengeProgressService(
    ChallengeProgressRepositories repositories,
    IXpAwarder xpAwarder,
    IUnitOfWork unitOfWork,
    IUserDateService userDateService) : IChallengeProgressService
{
    private const string MissionAccomplishedAchievementId = "mission_accomplished";

    public async Task EvaluateOnHabitLoggedAsync(Guid userId, Guid habitId, CancellationToken cancellationToken = default)
    {
        var links = await repositories.ParticipantHabits.FindAsync(cph => cph.HabitId == habitId, cancellationToken);
        if (links.Count == 0)
            return;

        var participantIds = links.Select(link => link.ChallengeParticipantId).ToList();
        var participants = await repositories.Participants.FindAsync(
            p => participantIds.Contains(p.Id) && p.UserId == userId && p.LeftAtUtc == null,
            cancellationToken);
        if (participants.Count == 0)
            return;

        var challengeIds = participants.Select(p => p.ChallengeId).Distinct().ToList();
        var challenges = await repositories.Challenges.FindTrackedAsync(
            c => challengeIds.Contains(c.Id) && c.Status == ChallengeStatus.Active && c.Type == ChallengeType.CoopGoal,
            q => q.Include(c => c.Participants).ThenInclude(p => p.LinkedHabits),
            cancellationToken);
        if (challenges.Count == 0)
            return;

        var today = await userDateService.GetUserTodayAsync(userId, cancellationToken);

        var anyCompleted = false;
        foreach (var challenge in challenges)
        {
            if (await TryCompleteCoopGoalAsync(challenge, today, cancellationToken))
                anyCompleted = true;
        }

        if (anyCompleted)
            await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> TryCompleteCoopGoalAsync(Challenge challenge, DateOnly today, CancellationToken cancellationToken)
    {
        if (!challenge.TargetCount.HasValue)
            return false;

        var contributingHabitIds = challenge.GetActiveParticipants()
            .SelectMany(p => p.LinkedHabits.Select(h => h.HabitId))
            .Distinct()
            .ToList();
        if (contributingHabitIds.Count == 0)
            return false;

        var windowEnd = challenge.PeriodEndUtc ?? today;
        var lastDay = windowEnd < today ? windowEnd : today;

        var logs = await repositories.HabitLogs.FindAsync(
            l => contributingHabitIds.Contains(l.HabitId)
                && l.Date >= challenge.PeriodStartUtc
                && l.Date <= lastDay,
            cancellationToken);

        var progress = ChallengeProgressCalculator.CalculateCoopGoalProgress(
            contributingHabitIds, logs, challenge.PeriodStartUtc, lastDay);

        if (progress < challenge.TargetCount.Value)
            return false;

        if (!challenge.MarkCompleted())
            return false;

        await AwardMissionAccomplishedAsync(challenge, cancellationToken);
        return true;
    }

    private async Task AwardMissionAccomplishedAsync(Challenge challenge, CancellationToken cancellationToken)
    {
        var participantUserIds = challenge.GetActiveParticipants().Select(p => p.UserId).Distinct().ToList();
        var users = await repositories.Users.FindTrackedAsync(u => participantUserIds.Contains(u.Id), cancellationToken);
        var alreadyEarned = await repositories.Achievements.FindAsync(
            a => participantUserIds.Contains(a.UserId) && a.AchievementId == MissionAccomplishedAchievementId,
            cancellationToken);
        var earnedUserIds = alreadyEarned.Select(a => a.UserId).ToHashSet();

        foreach (var user in users)
        {
            if (earnedUserIds.Contains(user.Id))
                continue;

            var earned = new HashSet<string>();
            var newAchievements = new List<(UserAchievement Entity, AchievementDefinition Definition)>();
            AchievementChecks.TryGrant(MissionAccomplishedAchievementId, user, earned, newAchievements);

            if (newAchievements.Count == 0)
                continue;

            await repositories.Achievements.AddAsync(newAchievements[0].Entity, cancellationToken);
            await xpAwarder.AwardAsync(
                user, newAchievements[0].Definition.XpReward, XpAwardSource.Achievement,
                newAchievements[0].Entity.Id, awardedAtUtc: DateTime.UtcNow, cancellationToken);

            LevelDefinitions.SyncLevel(user);
        }
    }
}
