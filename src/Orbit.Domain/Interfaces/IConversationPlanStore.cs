using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IConversationPlanStore
{
    Task<AiPlan?> ChangeAsync(Guid userId, Guid pendingOperationId, string status, CancellationToken cancellationToken = default);
}
