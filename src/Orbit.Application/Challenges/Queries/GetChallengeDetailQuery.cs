using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Challenges.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Challenges.Queries;

public record ChallengeParticipantResponse(
    Guid UserId,
    string Name,
    DateTime JoinedAtUtc);

public record ChallengeDetailResponse(
    Guid Id,
    Guid CreatorId,
    ChallengeType Type,
    string Title,
    string? Description,
    ChallengeStatus Status,
    int? TargetCount,
    int CurrentProgress,
    bool IsComplete,
    DateOnly PeriodStartUtc,
    DateOnly? PeriodEndUtc,
    string JoinCode,
    DateTime? CompletedAtUtc,
    DateTime CreatedAtUtc,
    IReadOnlyList<ChallengeParticipantResponse> Participants,
    IReadOnlyList<Guid> YourLinkedHabitIds);

public record GetChallengeDetailQuery(Guid UserId, Guid ChallengeId) : IRequest<Result<ChallengeDetailResponse>>;

public class GetChallengeDetailQueryHandler(
    IGenericRepository<Challenge> challengeRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<User> userRepository,
    IUserDateService userDateService) : IRequestHandler<GetChallengeDetailQuery, Result<ChallengeDetailResponse>>
{
    public async Task<Result<ChallengeDetailResponse>> Handle(GetChallengeDetailQuery request, CancellationToken cancellationToken)
    {
        var challenges = await challengeRepository.FindAsync(
            c => c.Id == request.ChallengeId,
            q => q.Include(c => c.Participants).ThenInclude(p => p.LinkedHabits),
            cancellationToken);

        var challenge = challenges.Count > 0 ? challenges[0] : null;
        if (challenge is null || !challenge.Participants.Any(p => p.UserId == request.UserId))
            return Result.Failure<ChallengeDetailResponse>(ErrorMessages.ChallengeNotFound);

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var windowEnd = challenge.PeriodEndUtc ?? today;
        var lastDay = windowEnd < today ? windowEnd : today;

        var activeParticipants = challenge.GetActiveParticipants();
        var contributingHabitSets = activeParticipants
            .Select(p => (IReadOnlyCollection<Guid>)p.LinkedHabits.Select(h => h.HabitId).ToList())
            .Where(set => set.Count > 0)
            .ToList();
        var contributingHabitIds = contributingHabitSets.SelectMany(set => set).Distinct().ToList();

        var logs = contributingHabitIds.Count > 0
            ? await habitLogRepository.FindAsync(
                l => contributingHabitIds.Contains(l.HabitId)
                    && l.Date >= challenge.PeriodStartUtc
                    && l.Date <= lastDay,
                cancellationToken)
            : [];

        var (currentProgress, isComplete) = ComputeProgress(challenge, contributingHabitIds, contributingHabitSets, logs, lastDay, today);

        var participants = await BuildParticipantRosterAsync(activeParticipants, cancellationToken);
        var yourLinkedHabitIds = challenge.Participants
            .Where(p => p.UserId == request.UserId && p.IsActive)
            .SelectMany(p => p.LinkedHabits.Select(h => h.HabitId))
            .ToList();

        return Result.Success(new ChallengeDetailResponse(
            challenge.Id,
            challenge.CreatorId,
            challenge.Type,
            challenge.Title,
            challenge.Description,
            challenge.Status,
            challenge.TargetCount,
            currentProgress,
            isComplete,
            challenge.PeriodStartUtc,
            challenge.PeriodEndUtc,
            challenge.JoinCode,
            challenge.CompletedAtUtc,
            challenge.CreatedAtUtc,
            participants,
            yourLinkedHabitIds));
    }

    private static (int CurrentProgress, bool IsComplete) ComputeProgress(
        Challenge challenge,
        IReadOnlyCollection<Guid> contributingHabitIds,
        IReadOnlyList<IReadOnlyCollection<Guid>> contributingHabitSets,
        IReadOnlyCollection<HabitLog> logs,
        DateOnly lastDay,
        DateOnly today)
    {
        if (challenge.Type == ChallengeType.CoopGoal)
        {
            var count = ChallengeProgressCalculator.CalculateCoopGoalProgress(
                contributingHabitIds, logs, challenge.PeriodStartUtc, lastDay);
            var reachedTarget = challenge.TargetCount.HasValue && count >= challenge.TargetCount.Value;
            var windowEnded = challenge.PeriodEndUtc.HasValue && today > challenge.PeriodEndUtc.Value;
            return (count, challenge.Status == ChallengeStatus.Completed || reachedTarget || windowEnded);
        }

        var streak = ChallengeProgressCalculator.CalculateSharedStreak(
            contributingHabitSets, logs, challenge.PeriodStartUtc, lastDay, today);
        return (streak, false);
    }

    private async Task<IReadOnlyList<ChallengeParticipantResponse>> BuildParticipantRosterAsync(
        IReadOnlyList<ChallengeParticipant> activeParticipants, CancellationToken cancellationToken)
    {
        var userIds = activeParticipants.Select(p => p.UserId).Distinct().ToList();
        var users = await userRepository.FindAsync(u => userIds.Contains(u.Id), cancellationToken);
        var namesByUserId = users.ToDictionary(u => u.Id, u => u.Name);

        return activeParticipants
            .Select(p => new ChallengeParticipantResponse(
                p.UserId,
                namesByUserId.TryGetValue(p.UserId, out var name) ? name : string.Empty,
                p.JoinedAtUtc))
            .ToList();
    }
}
