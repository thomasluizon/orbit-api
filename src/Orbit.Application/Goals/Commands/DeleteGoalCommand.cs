using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Commands;

public record DeleteGoalCommand(
    Guid UserId,
    Guid GoalId) : IRequest<Result>;

public class DeleteGoalCommandHandler(
    IGenericRepository<Goal> goalRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<DeleteGoalCommand, Result>
{
    public async Task<Result> Handle(DeleteGoalCommand request, CancellationToken cancellationToken)
    {
        var goal = await goalRepository.FindOneTrackedAsync(
            g => g.Id == request.GoalId && g.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (goal is null)
            return Result.Failure(ErrorMessages.GoalNotFound);

        goalRepository.Remove(goal);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
