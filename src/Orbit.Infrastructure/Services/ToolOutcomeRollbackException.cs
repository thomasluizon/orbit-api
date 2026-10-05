using Orbit.Application.Chat.Tools;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

public sealed class ToolOutcomeRollbackException(AgentPolicyDecision decision, ToolResult result)
    : Exception("Transactional tool returned a failed outcome.")
{
    public AgentPolicyDecision Decision { get; } = decision;
    public ToolResult Result { get; } = result;
}
