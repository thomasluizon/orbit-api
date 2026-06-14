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

        var orderedGoalIds = request.Positions
            .OrderBy(p => p.Position)
            .Select(p => p.GoalId)
            .ToList();

        var normalizedPosition = 0;
        foreach (var goalId in orderedGoalIds)
        {
            if (!goalMap.TryGetValue(goalId, out var goal))
                return Result.Failure(ErrorMessages.GoalNotFound);

            goal.UpdatePosition(normalizedPosition++);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
