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
    IGenericRepository<User> userRepository,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork) : IRequestHandler<UpdateGoalProgressCommand, Result>
{
    public async Task<Result> Handle(UpdateGoalProgressCommand request, CancellationToken cancellationToken)
    {
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

        // Update user streak (goal progress counts toward daily streak)
        var user = await userRepository.FindOneTrackedAsync(u => u.Id == request.UserId, cancellationToken: cancellationToken);
        if (user is not null)
        {
            var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
            user.UpdateStreak(today);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
