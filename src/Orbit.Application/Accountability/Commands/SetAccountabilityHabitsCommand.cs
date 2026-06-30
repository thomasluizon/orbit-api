using MediatR;
using Orbit.Application.Accountability.Services;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Accountability.Commands;

/// <summary>
/// Replaces the caller's own linked-habit set for a pair they participate in. Each habit must belong to
/// the caller (else HabitNotFound); an ended or non-participant pair resolves to a uniform PairNotFound.
/// </summary>
public record SetAccountabilityHabitsCommand(
    Guid UserId,
    Guid PairId,
    IReadOnlyList<Guid> HabitIds) : IRequest<Result>;

public class SetAccountabilityHabitsCommandHandler(
    SocialAccessGuard socialAccessGuard,
    AccountabilityPairService accountabilityPairService,
    IUnitOfWork unitOfWork) : IRequestHandler<SetAccountabilityHabitsCommand, Result>
{
    public async Task<Result> Handle(SetAccountabilityHabitsCommand request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError();

        var pair = await accountabilityPairService.FindParticipantPairAsync(request.PairId, request.UserId, cancellationToken);
        if (pair is null || pair.Status == AccountabilityPairStatus.Ended)
            return Result.Failure(ErrorMessages.PairNotFound);

        var linkResult = await accountabilityPairService.ReplaceLinkedHabitsAsync(
            pair, request.UserId, request.HabitIds, cancellationToken);
        if (linkResult.IsFailure)
            return linkResult;

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
