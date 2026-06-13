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
    IPayGateService payGate,
    IUnitOfWork unitOfWork) : IRequestHandler<ReorderGoalsCommand, Result>
{
    public async Task<Result> Handle(ReorderGoalsCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanAccessGoals(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck;

        var ids = request.Positions.Select(p => p.GoalId).ToHashSet();

        var goals = await goalRepository.FindTrackedAsync(
            g => ids.Contains(g.Id) && g.UserId == request.UserId,
            cancellationToken);

        var goalMap = goals.ToDictionary(g => g.Id);

        foreach (var update in request.Positions)
        {
            if (!goalMap.TryGetValue(update.GoalId, out var goal))
                return Result.Failure(ErrorMessages.GoalNotFound);

            goal.UpdatePosition(update.Position);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
