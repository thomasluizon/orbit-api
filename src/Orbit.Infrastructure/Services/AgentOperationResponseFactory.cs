using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

internal static class AgentOperationResponseFactory
{
    public static AgentExecuteOperationResponse UnknownOperation(string operationId, string? access)
    {
        return new AgentExecuteOperationResponse(
            new AgentOperationResult(
                operationId,
                operationId,
                AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                AgentOperationStatus.UnsupportedByPolicy,
                PolicyReason: "unsupported_by_policy",
                Access: access),
            PolicyDenial: new AgentPolicyDenial(
                operationId,
                operationId,
                AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                "unsupported_by_policy"));
    }

    public static AgentExecuteOperationResponse MissingTool(
        string operationId,
        bool isMutation,
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
                PolicyReason: "missing_tool_executor",
                Access: isMutation ? "write" : "read"),
            PolicyDenial: new AgentPolicyDenial(
                operationId,
                operationId,
                riskClass,
                confirmationRequirement,
                "missing_tool_executor"));
    }

    public static AgentExecuteOperationResponse Denied(
        string operationId,
        bool isMutation,
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
                PolicyReason: policyReason,
                Access: isMutation ? "write" : "read"),
            PolicyDenial: new AgentPolicyDenial(
                operationId,
                operationId,
                riskClass,
                confirmationRequirement,
                denialReason));
    }

    public static AgentExecuteOperationResponse ConfirmationRequired(
        string operationId,
        bool isMutation,
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
                PendingOperationId: pendingOperation?.Id,
                Access: isMutation ? "write" : "read"),
            PendingOperation: pendingOperation);
    }

    public static AgentExecuteOperationResponse Failed(
        string operationId,
        bool isMutation,
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
            PolicyReason: "unexpected_error",
            Access: isMutation ? "write" : "read"));
    }

    public static AgentExecuteOperationResponse ToolOutcome(
        string operationId,
        bool isMutation,
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
                ErrorCode: result.ErrorCode,
                Payload: result.Payload,
                Access: isMutation ? "write" : "read"),
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
