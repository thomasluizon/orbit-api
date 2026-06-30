using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Challenges.Commands;

public record LeaveChallengeCommand(Guid UserId, Guid ChallengeId) : IRequest<Result>;

public class LeaveChallengeCommandHandler(
    IGenericRepository<Challenge> challengeRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<LeaveChallengeCommand, Result>
{
    public async Task<Result> Handle(LeaveChallengeCommand request, CancellationToken cancellationToken)
    {
        var challenge = await challengeRepository.FindOneTrackedAsync(
            c => c.Id == request.ChallengeId,
            q => q.Include(c => c.Participants),
            cancellationToken);

        if (challenge is null)
            return Result.Failure(ErrorMessages.ChallengeNotFound);

        if (!challenge.TryLeave(request.UserId))
            return Result.Failure(ErrorMessages.NotChallengeParticipant);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
