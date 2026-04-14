using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Commands;

public record UpdateGoalProgressCommand(
    Guid UserId,
    Guid GoalId,
    decimal NewValue,
    string? Note = null) : IRequest<Result>;

public class UpdateGoalProgressCommandHandler(
    IGenericRepository<Goal> goalRepository,
    IGenericRepository<GoalProgressLog> progressLogRepository,
    IPayGateService payGate,
    IUnitOfWork unitOfWork) : IRequestHandler<UpdateGoalProgressCommand, Result>
{
    public async Task<Result> Handle(UpdateGoalProgressCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanAccessGoals(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck;

        var goal = await goalRepository.FindOneTrackedAsync(
            g => g.Id == request.GoalId && g.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (goal is null)
            return Result.Failure(ErrorMessages.GoalNotFound, ErrorCodes.GoalNotFound);

        var previousValue = goal.CurrentValue;
        var progressLog = GoalProgressLog.Create(goal.Id, previousValue, request.NewValue, request.Note);
        await progressLogRepository.AddAsync(progressLog, cancellationToken);

        var result = goal.UpdateProgress(request.NewValue);
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
