using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.UserFacts.Commands;

public record BulkDeleteUserFactsCommand(
    Guid UserId,
    IReadOnlyList<Guid> FactIds) : IRequest<Result<int>>;

public class BulkDeleteUserFactsCommandHandler(
    IGenericRepository<UserFact> userFactRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<BulkDeleteUserFactsCommand, Result<int>>
{
    public async Task<Result<int>> Handle(BulkDeleteUserFactsCommand request, CancellationToken cancellationToken)
    {
        var deleted = 0;

        foreach (var factId in request.FactIds)
        {
            var fact = await userFactRepository.FindOneTrackedAsync(
                f => f.Id == factId && f.UserId == request.UserId,
                cancellationToken: cancellationToken);

            if (fact is null)
                continue;

            fact.SoftDelete();
            deleted++;
        }

        if (deleted > 0)
            await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(deleted);
    }
}
