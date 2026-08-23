using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Challenges.Services;

/// <summary>Groups the repositories the challenge progress seam touches to keep the constructor small.</summary>
public record ChallengeProgressRepositories(
    IGenericRepository<Challenge> Challenges,
    IGenericRepository<ChallengeParticipant> Participants,
    IGenericRepository<ChallengeParticipantHabit> ParticipantHabits,
    IGenericRepository<HabitLog> HabitLogs);

public class ChallengeProgressService(
    ChallengeProgressRepositories repositories,
    IUnitOfWork unitOfWork,
    IUserDateService userDateService) : IChallengeProgressService
{
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

        return true;
    }
}
