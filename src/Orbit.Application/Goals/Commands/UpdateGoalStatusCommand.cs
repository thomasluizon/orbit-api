using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Commands;

public record UpdateGoalStatusCommand(
    Guid UserId,
    Guid GoalId,
    GoalStatus NewStatus) : IRequest<Result>, IConcurrencyRetryable;

public class UpdateGoalStatusCommandHandler(
    IGenericRepository<Goal> goalRepository,
    IGoalCompletionService goalCompletionService,
    IUnitOfWork unitOfWork,
    IUserDateService userDateService,
    IMemoryCache cache) : IRequestHandler<UpdateGoalStatusCommand, Result>
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
            _ => Result.Failure(ErrorMessages.InvalidGoalStatus)
        };

        if (result.IsFailure) return result;

        if (request.NewStatus == GoalStatus.Completed)
            await goalCompletionService.SaveCompletedGoalAsync(request.UserId, goal.Id, cancellationToken);
        else
            await unitOfWork.SaveChangesAsync(cancellationToken);

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);

        return Result.Success();
    }

}
