using MediatR;
using Orbit.Application.Accountability.Services;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Accountability.Commands;

/// <summary>
/// Ends an accountability pair the caller participates in, from either side and from either Pending or
/// Accepted. An already-ended or non-participant pair resolves to a uniform PairNotFound (no enumeration).
/// </summary>
public record EndAccountabilityPairCommand(Guid UserId, Guid PairId) : IRequest<Result>;

public class EndAccountabilityPairCommandHandler(
    SocialAccessGuard socialAccessGuard,
    AccountabilityPairService accountabilityPairService,
    IUnitOfWork unitOfWork) : IRequestHandler<EndAccountabilityPairCommand, Result>
{
    public async Task<Result> Handle(EndAccountabilityPairCommand request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError();

        var pair = await accountabilityPairService.FindParticipantPairAsync(request.PairId, request.UserId, cancellationToken);
        if (pair is null || pair.Status == AccountabilityPairStatus.Ended)
            return Result.Failure(ErrorMessages.PairNotFound);

        pair.End();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
