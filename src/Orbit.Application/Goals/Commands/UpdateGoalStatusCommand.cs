using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Commands;

public record UpdateGoalStatusCommand(
    Guid UserId,
    Guid GoalId,
    GoalStatus NewStatus) : IRequest<Result>;

public class UpdateGoalStatusCommandHandler(
    IGenericRepository<Goal> goalRepository,
    IGamificationService gamificationService,
    IUnitOfWork unitOfWork) : IRequestHandler<UpdateGoalStatusCommand, Result>
{
    public async Task<Result> Handle(UpdateGoalStatusCommand request, CancellationToken cancellationToken)
    {
        var goal = await goalRepository.FindOneTrackedAsync(
            g => g.Id == request.GoalId && g.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (goal is null)
            return Result.Failure(ErrorMessages.GoalNotFound);

        var result = request.NewStatus switch
        {
            GoalStatus.Completed => goal.MarkCompleted(),
            GoalStatus.Abandoned => goal.MarkAbandoned(),
            GoalStatus.Active => goal.Reactivate(),
            _ => Result.Failure("Invalid status.")
        };

        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (request.NewStatus == GoalStatus.Completed)
        {
            try
            {
                await gamificationService.ProcessGoalCompleted(request.UserId, cancellationToken);
            }
            catch { /* gamification failure should not block goal status update */ }
        }

        return Result.Success();
    }
}
