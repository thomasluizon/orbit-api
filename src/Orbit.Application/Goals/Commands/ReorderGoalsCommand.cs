using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Commands;

public record GoalPositionUpdate(Guid GoalId, int Position);

public record ReorderGoalsCommand(
    Guid UserId,
    IReadOnlyList<GoalPositionUpdate> Positions) : IRequest<Result>;

public class ReorderGoalsCommandHandler(
    IGenericRepository<Goal> goalRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<ReorderGoalsCommand, Result>
{
    public async Task<Result> Handle(ReorderGoalsCommand request, CancellationToken cancellationToken)
    {
        foreach (var update in request.Positions)
        {
            var goal = await goalRepository.FindOneTrackedAsync(
                g => g.Id == update.GoalId && g.UserId == request.UserId,
                cancellationToken: cancellationToken);

            if (goal is null)
                return Result.Failure(ErrorMessages.GoalNotFound, ErrorCodes.GoalNotFound);

            goal.UpdatePosition(update.Position);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
