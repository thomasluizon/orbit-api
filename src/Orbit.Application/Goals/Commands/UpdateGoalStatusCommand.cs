using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Commands;

public record UpdateGoalStatusCommand(
    Guid UserId,
    Guid GoalId,
    GoalStatus NewStatus) : IRequest<Result>, IConcurrencyRetryable;

public partial class UpdateGoalStatusCommandHandler(
    IGenericRepository<Goal> goalRepository,
    IPayGateService payGate,
    IGamificationService gamificationService,
    IUnitOfWork unitOfWork,
    IUserDateService userDateService,
    IMemoryCache cache,
    ILogger<UpdateGoalStatusCommandHandler> logger) : IRequestHandler<UpdateGoalStatusCommand, Result>
{
    public async Task<Result> Handle(UpdateGoalStatusCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanAccessGoals(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck;

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

        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (request.NewStatus == GoalStatus.Completed)
        {
            try
            {
                await gamificationService.ProcessGoalCompleted(request.UserId, cancellationToken);
            }
            catch (Exception ex)
            {
                LogGamificationGoalCompletionFailed(logger, ex, request.UserId);
            }
        }

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);

        return Result.Success();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Gamification processing failed for goal completion by user {UserId}")]
    private static partial void LogGamificationGoalCompletionFailed(ILogger logger, Exception ex, Guid userId);
}
