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

public record UpdateGoalCommand(
    Guid UserId,
    Guid GoalId,
    string Title,
    string? Description,
    decimal TargetValue,
    string Unit,
    DateOnly? Deadline) : IRequest<Result>, IConcurrencyRetryable;

public class UpdateGoalCommandHandler(
    GoalRepositories repos,
    IUserDateService userDateService,
    IGoalCompletionService goalCompletionService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<UpdateGoalCommand, Result>
{
    public async Task<Result> Handle(UpdateGoalCommand request, CancellationToken cancellationToken)
    {
        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        if (request.Deadline is { } deadline && deadline < today)
            return Result.Failure(ErrorMessages.DeadlineInPast);

        var goal = await repos.Goals.FindOneTrackedAsync(
            g => g.Id == request.GoalId && g.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (goal is null)
            return Result.Failure(ErrorMessages.GoalNotFound);

        var currentValue = goal.CurrentValue;
        var result = goal.Update(request.Title, request.Description, request.TargetValue, request.Unit, request.Deadline);
        if (result.IsFailure) return result;

        if (result.Value == GoalEditTransition.Completed)
        {
            var progressLog = GoalProgressLog.Create(goal.Id, currentValue, currentValue);
            await repos.ProgressLogs.AddAsync(progressLog, cancellationToken);
        }

        if (result.Value == GoalEditTransition.Completed)
            await goalCompletionService.SaveCompletedGoalAsync(request.UserId, goal.Id, cancellationToken);
        else
            await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);

        return Result.Success();
    }

}
