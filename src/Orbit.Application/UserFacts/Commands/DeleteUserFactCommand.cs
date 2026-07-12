using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.UserFacts.Commands;

public record DeleteUserFactCommand(Guid UserId, Guid FactId) : IRequest<Result>;

public class DeleteUserFactCommandHandler(
    IGenericRepository<UserFact> userFactRepository,
    IPayGateService payGate,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<DeleteUserFactCommand, Result>
{
    public async Task<Result> Handle(DeleteUserFactCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanManageUserFacts(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck;

        var fact = await userFactRepository.FindOneTrackedAsync(
            f => f.Id == request.FactId && f.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (fact is null)
            return Result.Failure(ErrorMessages.FactNotFound);

        fact.SoftDelete();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        cache.Remove(ReferenceCacheKeys.UserFacts(request.UserId));

        return Result.Success();
    }
}
