using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.UserFacts.Commands;

public record UpdateUserFactCommand(Guid UserId, Guid FactId, string FactText, string? Category) : IRequest<Result>;

public class UpdateUserFactCommandHandler(
    IGenericRepository<UserFact> userFactRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<UpdateUserFactCommand, Result>
{
    public async Task<Result> Handle(UpdateUserFactCommand request, CancellationToken cancellationToken)
    {
        var fact = await userFactRepository.FindOneTrackedAsync(
            f => f.Id == request.FactId && f.UserId == request.UserId && !f.IsDeleted,
            cancellationToken: cancellationToken);

        if (fact is null)
            return Result.Failure(ErrorMessages.FactNotFound, ErrorCodes.FactNotFound);

        fact.Update(request.FactText, request.Category);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
