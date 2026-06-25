using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

internal static class AgentOperationResponseFactory
{
    public static AgentExecuteOperationResponse UnknownOperation(string operationId)
    {
        return new AgentExecuteOperationResponse(
            new AgentOperationResult(
                operationId,
                operationId,
                AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                AgentOperationStatus.UnsupportedByPolicy,
                PolicyReason: "unsupported_by_policy"),
            PolicyDenial: new AgentPolicyDenial(
                operationId,
                operationId,
                AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                "unsupported_by_policy"));
    }

    public static AgentExecuteOperationResponse MissingTool(
        string operationId,
        AgentRiskClass riskClass,
        AgentConfirmationRequirement confirmationRequirement,
        string summary)
    {
        return new AgentExecuteOperationResponse(
            new AgentOperationResult(
                operationId,
                operationId,
                riskClass,
                confirmationRequirement,
                AgentOperationStatus.UnsupportedByPolicy,
                Summary: summary,
                PolicyReason: "missing_tool_executor"),
            PolicyDenial: new AgentPolicyDenial(
                operationId,
                operationId,
                riskClass,
                confirmationRequirement,
                "missing_tool_executor"));
    }

    public static AgentExecuteOperationResponse Denied(
        string operationId,
        AgentRiskClass riskClass,
        AgentConfirmationRequirement confirmationRequirement,
        string summary,
        string? policyReason,
        string denialReason)
    {
        return new AgentExecuteOperationResponse(
            new AgentOperationResult(
                operationId,
                operationId,
                riskClass,
                confirmationRequirement,
                AgentOperationStatus.Denied,
                Summary: summary,
                PolicyReason: policyReason),
            PolicyDenial: new AgentPolicyDenial(
                operationId,
                operationId,
                riskClass,
                confirmationRequirement,
                denialReason));
    }

    public static AgentExecuteOperationResponse ConfirmationRequired(
        string operationId,
        AgentRiskClass riskClass,
        AgentConfirmationRequirement confirmationRequirement,
        string summary,
        string? policyReason,
        PendingAgentOperation? pendingOperation)
    {
        return new AgentExecuteOperationResponse(
            new AgentOperationResult(
                operationId,
                operationId,
                riskClass,
                confirmationRequirement,
                AgentOperationStatus.PendingConfirmation,
                Summary: summary,
                PolicyReason: policyReason,
                PendingOperationId: pendingOperation?.Id),
            PendingOperation: pendingOperation);
    }

    public static AgentExecuteOperationResponse Failed(
        string operationId,
        AgentRiskClass riskClass,
        AgentConfirmationRequirement confirmationRequirement,
        string summary)
    {
        return new AgentExecuteOperationResponse(new AgentOperationResult(
            operationId,
            operationId,
            riskClass,
            confirmationRequirement,
            AgentOperationStatus.Failed,
            Summary: summary,
            PolicyReason: "unexpected_error"));
    }

    public static AgentExecuteOperationResponse ToolOutcome(
        string operationId,
        AgentRiskClass riskClass,
        AgentConfirmationRequirement confirmationRequirement,
        string summary,
        ToolResult result,
        AgentOperationStatus outcomeStatus,
        bool isPayGateDenial)
    {
        return new AgentExecuteOperationResponse(
            new AgentOperationResult(
                operationId,
                operationId,
                riskClass,
                confirmationRequirement,
                outcomeStatus,
                Summary: summary,
                TargetId: result.EntityId,
                TargetName: result.EntityName,
                PolicyReason: result.Success ? null : result.Error,
                Payload: result.Payload),
            PolicyDenial: isPayGateDenial
                ? new AgentPolicyDenial(
                    operationId,
                    operationId,
                    riskClass,
                    confirmationRequirement,
                    result.Error ?? Result.PayGateErrorCode)
                : null);
    }
}
