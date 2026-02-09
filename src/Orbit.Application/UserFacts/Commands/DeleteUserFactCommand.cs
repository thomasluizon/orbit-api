using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.UserFacts.Commands;

public record DeleteUserFactCommand(Guid UserId, Guid FactId) : IRequest<Result>;

public class DeleteUserFactCommandHandler(
    IGenericRepository<UserFact> userFactRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<DeleteUserFactCommand, Result>
{
    public async Task<Result> Handle(DeleteUserFactCommand request, CancellationToken cancellationToken)
    {
        var fact = await userFactRepository.FindOneTrackedAsync(
            f => f.Id == request.FactId && f.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (fact is null)
            return Result.Failure("Fact not found.");

        fact.SoftDelete();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
