using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Commands;

public record UpdateGoalProgressCommand(
    Guid UserId,
    Guid GoalId,
    decimal NewValue,
    string? Note = null) : IRequest<Result>, IIdempotentCommand;

public class UpdateGoalProgressCommandHandler(
    GoalRepositories repos,
    IGoalCompletionService goalCompletionService,
    IUnitOfWork unitOfWork,
    IUserDateService userDateService,
    IMemoryCache cache) : IRequestHandler<UpdateGoalProgressCommand, Result>
{
    public async Task<Result> Handle(UpdateGoalProgressCommand request, CancellationToken cancellationToken)
    {
        var saved = await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
        {
            var justCompleted = false;
            var result = await ConcurrencyRetry.ExecuteAsync(
                repos.Goals,
                unitOfWork,
                ct => repos.Goals.FindOneTrackedAsync(
                    g => g.Id == request.GoalId && g.UserId == request.UserId,
                    q => q.Include(g => g.Habits),
                    ct),
                async goal =>
                {
                    var previousValue = goal.CurrentValue;
                    var updateResult = goal.UpdateProgress(request.NewValue);
                    if (updateResult.IsFailure)
                        return updateResult;

                    justCompleted = updateResult.Value;
                    var progressLog = GoalProgressLog.Create(goal.Id, previousValue, request.NewValue, request.Note);
                    await repos.ProgressLogs.AddAsync(progressLog, transactionToken);
                    return Result.Success();
                },
                ErrorMessages.GoalNotFound,
                transactionToken);

            if (result.IsSuccess && justCompleted)
                await goalCompletionService.SaveCompletedGoalAsync(request.UserId, request.GoalId, transactionToken);

            return result;
        }, cancellationToken);

        if (saved.IsFailure)
            return saved;

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);

        return Result.Success();
    }

}
