using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.UserFacts.Commands;

public record BulkDeleteUserFactsCommand(
    Guid UserId,
    IReadOnlyList<Guid> FactIds) : IRequest<Result<int>>;

public class BulkDeleteUserFactsCommandHandler(
    IGenericRepository<UserFact> userFactRepository,
    IPayGateService payGate,
    IUnitOfWork unitOfWork) : IRequestHandler<BulkDeleteUserFactsCommand, Result<int>>
{
    public async Task<Result<int>> Handle(BulkDeleteUserFactsCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanManageUserFacts(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<int>();

        var factIds = request.FactIds.ToHashSet();
        var facts = await userFactRepository.FindTrackedAsync(
            f => factIds.Contains(f.Id) && f.UserId == request.UserId,
            cancellationToken);

        foreach (var fact in facts)
            fact.SoftDelete();

        if (facts.Count > 0)
            await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(facts.Count);
    }
}
