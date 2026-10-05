using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

public sealed class AgentPolicyExecutionException(AgentPolicyDecision decision, Exception failure)
    : Exception("Agent operation failed after policy evaluation.", failure)
{
    public AgentPolicyDecision Decision { get; } = decision;
    public Exception Failure { get; } = failure;
}
