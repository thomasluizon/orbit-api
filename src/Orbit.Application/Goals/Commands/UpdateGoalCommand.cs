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

public record UpdateGoalCommand(
    Guid UserId,
    Guid GoalId,
    string Title,
    string? Description,
    decimal TargetValue,
    string Unit,
    DateOnly? Deadline) : IRequest<Result>, IConcurrencyRetryable;

public partial class UpdateGoalCommandHandler(
    GoalRepositories repos,
    IPayGateService payGate,
    IUserDateService userDateService,
    IGamificationService gamificationService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    ILogger<UpdateGoalCommandHandler> logger) : IRequestHandler<UpdateGoalCommand, Result>
{
    public async Task<Result> Handle(UpdateGoalCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanAccessGoals(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck;

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

        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (result.Value == GoalEditTransition.Completed)
            await ProcessGoalCompletionSafeAsync(request.UserId, cancellationToken);

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);

        return Result.Success();
    }

    private async Task ProcessGoalCompletionSafeAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            await gamificationService.ProcessGoalCompleted(userId, cancellationToken);
        }
        catch (Exception ex)
        {
            LogGamificationGoalCompletionFailed(logger, ex, userId);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Gamification processing failed for goal completion by user {UserId}")]
    private static partial void LogGamificationGoalCompletionFailed(ILogger logger, Exception ex, Guid userId);
}
