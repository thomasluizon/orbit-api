using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Mcp;

internal static class SharedToolExecution
{
    internal static AgentOperationExecutor Create(IAiTool tool, IUnitOfWork unitOfWork)
    {
        var catalog = new AgentCatalogService([tool]);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>()).Returns(new AgentPolicyDecision(
            AgentPolicyDecisionStatus.Allowed, catalog.GetCapabilityByChatTool(tool.Name)!));
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var stepUp = Substitute.For<IAgentStepUpAuthorizationBridge>();
        stepUp.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentConfirmationRequirement?)null);
        return new AgentOperationExecutor(
            catalog, policy, Substitute.For<IAgentAuditService>(), ownership, stepUp,
            new AiToolRegistry([tool]), Substitute.For<IPendingOperationChangePreviewer>(),
            Substitute.For<IPendingAgentOperationStore>(), unitOfWork, NullLogger<AgentOperationExecutor>.Instance);
    }
}
