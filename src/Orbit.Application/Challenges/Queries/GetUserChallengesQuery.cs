using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Challenges.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Challenges.Queries;

public record ChallengeListItemResponse(
    Guid Id,
    ChallengeType Type,
    string Title,
    ChallengeStatus Status,
    int? TargetCount,
    int CurrentProgress,
    bool IsComplete,
    int ParticipantCount,
    DateOnly PeriodStartUtc,
    DateOnly? PeriodEndUtc,
    string JoinCode,
    bool HasLinkedHabits);

public record GetUserChallengesQuery(Guid UserId) : IRequest<Result<IReadOnlyList<ChallengeListItemResponse>>>;

public class GetUserChallengesQueryHandler(
    IGenericRepository<Challenge> challengeRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IUserDateService userDateService) : IRequestHandler<GetUserChallengesQuery, Result<IReadOnlyList<ChallengeListItemResponse>>>
{
    public async Task<Result<IReadOnlyList<ChallengeListItemResponse>>> Handle(GetUserChallengesQuery request, CancellationToken cancellationToken)
    {
        var challenges = await challengeRepository.FindAsync(
            c => c.Participants.Any(p => p.UserId == request.UserId && p.LeftAtUtc == null),
            q => q.Include(c => c.Participants).ThenInclude(p => p.LinkedHabits),
            cancellationToken);

        if (challenges.Count == 0)
            return Result.Success<IReadOnlyList<ChallengeListItemResponse>>([]);

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        var contributingHabitIds = challenges
            .SelectMany(c => ChallengeProgressCalculator.GetContributingHabitIds(c))
            .Distinct()
            .ToList();

        var logs = contributingHabitIds.Count > 0
            ? await habitLogRepository.FindAsync(
                l => contributingHabitIds.Contains(l.HabitId),
                cancellationToken)
            : [];

        var items = challenges
            .OrderBy(c => c.Status == ChallengeStatus.Completed)
            .ThenByDescending(c => c.CreatedAtUtc)
            .Select(c => BuildListItem(c, request.UserId, logs, today))
            .ToList();

        return Result.Success<IReadOnlyList<ChallengeListItemResponse>>(items);
    }

    private static ChallengeListItemResponse BuildListItem(
        Challenge challenge, Guid userId, IReadOnlyCollection<HabitLog> logs, DateOnly today)
    {
        var windowEnd = challenge.PeriodEndUtc ?? today;
        var lastDay = windowEnd < today ? windowEnd : today;
        var (currentProgress, isComplete) = ChallengeProgressCalculator.ComputeProgress(challenge, logs, lastDay, today);

        var activeParticipants = challenge.GetActiveParticipants();
        var hasLinkedHabits = activeParticipants.Any(p => p.UserId == userId && p.LinkedHabits.Count > 0);

        return new ChallengeListItemResponse(
            challenge.Id,
            challenge.Type,
            challenge.Title,
            challenge.Status,
            challenge.TargetCount,
            currentProgress,
            isComplete,
            activeParticipants.Count,
            challenge.PeriodStartUtc,
            challenge.PeriodEndUtc,
            challenge.JoinCode,
            hasLinkedHabits);
    }
}
