using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task PersistExecutionResultsAsync(
        Guid userId,
        IReadOnlyList<ActionResult> actionResults,
        CancellationToken cancellationToken)
    {
        await execution.UnitOfWork.SaveChangesAsync(cancellationToken);
        if (RequiresStreakRecalculation(actionResults))
        {
            await ConcurrencyRetry.SaveWithRetryAsync(
                execution.UnitOfWork,
                ct => execution.UserStreakService.RecalculateAsync(userId, ct),
                cancellationToken);
        }
    }

    private static bool RequiresStreakRecalculation(IEnumerable<ActionResult> actionResults)
    {
        return actionResults.Any(action => action.Status == ActionStatus.Success && action.Type is "LogHabit" or "BulkLogHabits" or "DeleteHabit");
    }

    /// <summary>
    /// Fires off background work for fact extraction and AI message counter increment.
    /// Runs in a separate DI scope so it doesn't block the response.
    /// </summary>
    private void RunBackgroundPostResponseWork(
        Guid userId,
        string userMessage,
        string? aiMessage,
        bool shouldExtractFacts,
        IReadOnlyList<UserFact> existingFacts)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = execution.ServiceScopeFactory.CreateScope();
                var bgUnitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var bgUserRepo = scope.ServiceProvider.GetRequiredService<IGenericRepository<User>>();
                var bgLogger = scope.ServiceProvider.GetRequiredService<ILogger<ProcessUserChatCommandHandler>>();

                if (shouldExtractFacts)
                    await SubmitFactExtractionBatchAsync(scope, userId, userMessage, aiMessage, existingFacts);

                await IncrementAiMessageCountAsync(bgUserRepo, bgUnitOfWork, userId, bgLogger);
            }
            catch (Exception ex)
            {
                LogBackgroundPostResponseFailed(logger, ex);
            }
        }, CancellationToken.None);
    }

    private static async Task SubmitFactExtractionBatchAsync(
        IServiceScope scope,
        Guid userId,
        string userMessage,
        string? aiMessage,
        IReadOnlyList<UserFact> existingFacts)
    {
        var bgFactService = scope.ServiceProvider.GetRequiredService<IFactExtractionService>();
        await bgFactService.SubmitBatchAsync(userMessage: userMessage, aiResponse: aiMessage,
            existingFacts: existingFacts, userId: userId, cancellationToken: CancellationToken.None);
    }

    private static async Task IncrementAiMessageCountAsync(
        IGenericRepository<User> bgUserRepo,
        IUnitOfWork bgUnitOfWork,
        Guid userId,
        ILogger bgLogger)
    {
        try
        {
            await ConcurrencyRetry.ExecuteAsync(
                bgUserRepo,
                bgUnitOfWork,
                ct => bgUserRepo.FindOneTrackedAsync(u => u.Id == userId, cancellationToken: ct),
                user =>
                {
                    user.IncrementAiMessageCount();
                    return Task.FromResult(Result.Success());
                },
                ErrorMessages.UserNotFound,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogBackgroundMessageCounterFailed(bgLogger, ex);
        }
    }
}
