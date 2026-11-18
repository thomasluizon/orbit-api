using NSubstitute;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Tests.Shared;

internal static class HeldPreviewExecutor
{
    /// <summary>An executor that holds each operation for a confirmation and returns no preview.</summary>
    public static IAgentOperationExecutor Create()
    {
        var executor = Substitute.For<IAgentOperationExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var operationId = call.Arg<AgentExecuteOperationRequest>().OperationId;
            return new AgentExecuteOperationResponse(new AgentOperationResult(operationId, operationId, AgentRiskClass.Low,
                AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation));
        });
        return executor;
    }
}
