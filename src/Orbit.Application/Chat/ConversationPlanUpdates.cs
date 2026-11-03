using Microsoft.Extensions.Logging;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public static class ConversationPlanUpdates
{
    public const string ExecutionAuditSource = "pending_operation_execution";

    public static async Task<AstraPlan?> AfterCommitAsync(IConversationPlanStore? plans, Guid userId, Guid pendingOperationId,
        string status, ILogger logger, CancellationToken cancellationToken)
    {
        if (plans is null) return null;
        try
        {
            return await plans.ChangeAsync(userId, pendingOperationId, status, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to persist plan status {PlanStatus} for pending operation {PendingOperationId}", status, pendingOperationId);
            return null;
        }
    }
}
