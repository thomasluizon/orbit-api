using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public sealed record ExecuteAgentMutationCommand(AgentExecuteOperationRequest Request)
    : IRequest<AgentExecuteOperationResponse>, IIdempotentCommand;

public interface IAgentMutationRunner
{
    Task<AgentExecuteOperationResponse> ExecuteAsync(AgentExecuteOperationRequest request, CancellationToken cancellationToken);
}

public sealed class ExecuteAgentMutationCommandHandler(IAgentMutationRunner runner)
    : IRequestHandler<ExecuteAgentMutationCommand, AgentExecuteOperationResponse>
{
    public Task<AgentExecuteOperationResponse> Handle(ExecuteAgentMutationCommand request, CancellationToken cancellationToken) =>
        runner.ExecuteAsync(request.Request, cancellationToken);
}
