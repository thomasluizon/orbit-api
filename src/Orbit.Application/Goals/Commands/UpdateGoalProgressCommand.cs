using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Commands;

public record UpdateGoalProgressCommand(
    Guid UserId,
    Guid GoalId,
    decimal NewValue,
    string? Note = null) : IRequest<Result>, IIdempotentCommand;

public partial class UpdateGoalProgressCommandHandler(
    IGenericRepository<Goal> goalRepository,
    IGenericRepository<GoalProgressLog> progressLogRepository,
    IPayGateService payGate,
    IGamificationService gamificationService,
    IUnitOfWork unitOfWork,
    ILogger<UpdateGoalProgressCommandHandler> logger) : IRequestHandler<UpdateGoalProgressCommand, Result>
{
    public async Task<Result> Handle(UpdateGoalProgressCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanAccessGoals(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck;

        var justCompleted = false;

        var saved = await ConcurrencyRetry.ExecuteAsync(
            goalRepository,
            unitOfWork,
            ct => goalRepository.FindOneTrackedAsync(
                g => g.Id == request.GoalId && g.UserId == request.UserId, cancellationToken: ct),
            async goal =>
            {
                var previousValue = goal.CurrentValue;
                var result = goal.UpdateProgress(request.NewValue);
                if (result.IsFailure)
                    return result;

                justCompleted = result.Value;
                var progressLog = GoalProgressLog.Create(goal.Id, previousValue, request.NewValue, request.Note);
                await progressLogRepository.AddAsync(progressLog, cancellationToken);
                return Result.Success();
            },
            ErrorMessages.GoalNotFound,
            cancellationToken);

        if (saved.IsFailure)
            return saved;

        if (justCompleted)
            await ProcessGoalCompletionSafeAsync(request.UserId, cancellationToken);

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
