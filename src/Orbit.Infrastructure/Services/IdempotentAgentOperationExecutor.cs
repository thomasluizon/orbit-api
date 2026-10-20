using MediatR;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

public sealed class IdempotentAgentOperationExecutor(ISender sender, AgentOperationExecutor executor, IAgentCatalogService catalog)
    : IAgentOperationExecutor
{
    public Task<AgentExecuteOperationResponse> ExecuteAsync(AgentExecuteOperationRequest request, CancellationToken cancellationToken = default) =>
        request.Surface == AgentExecutionSurface.Mcp && catalog.GetOperation(request.OperationId)?.IsMutation == true
            ? sender.Send(new ExecuteAgentMutationCommand(request), cancellationToken)
            : executor.ExecuteAsync(request, cancellationToken);
}
