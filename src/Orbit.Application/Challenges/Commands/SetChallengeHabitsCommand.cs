using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Challenges.Commands;

/// <summary>
/// Replaces the caller's own linked-habit set for a challenge they actively participate in. Each habit
/// must belong to the caller (else HabitNotFound); a missing challenge is ChallengeNotFound and a
/// non-participant (or one who has left) resolves to NotChallengeParticipant.
/// </summary>
public record SetChallengeHabitsCommand(
    Guid UserId,
    Guid ChallengeId,
    IReadOnlyList<Guid> HabitIds) : IRequest<Result>;

public class SetChallengeHabitsCommandHandler(
    SocialAccessGuard socialAccessGuard,
    IGenericRepository<Challenge> challengeRepository,
    IGenericRepository<Habit> habitRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<SetChallengeHabitsCommand, Result>
{
    public async Task<Result> Handle(SetChallengeHabitsCommand request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError();

        var challenge = await challengeRepository.FindOneTrackedAsync(
            c => c.Id == request.ChallengeId,
            q => q.Include(c => c.Participants).ThenInclude(p => p.LinkedHabits),
            cancellationToken);

        if (challenge is null)
            return Result.Failure(ErrorMessages.ChallengeNotFound);

        var distinctIds = request.HabitIds.Distinct().ToList();
        var owned = await habitRepository.CountAsync(
            h => h.UserId == request.UserId && distinctIds.Contains(h.Id),
            cancellationToken);
        if (owned != distinctIds.Count)
            return Result.Failure(ErrorMessages.HabitNotFound);

        if (!challenge.TrySetParticipantHabits(request.UserId, distinctIds))
            return Result.Failure(ErrorMessages.NotChallengeParticipant);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
