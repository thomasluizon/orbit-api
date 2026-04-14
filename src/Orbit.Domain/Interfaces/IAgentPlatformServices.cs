using System.Text.Json;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IAgentCatalogService
{
    IReadOnlyList<AgentCapability> GetCapabilities();
    IReadOnlyList<AgentOperation> GetOperations();
    IReadOnlyList<AppSurface> GetSurfaces();
    IReadOnlyList<UserDataCatalogEntry> GetUserDataCatalog();
    AgentCapability? GetCapability(string capabilityId);
    AgentOperation? GetOperation(string operationId);
    AgentCapability? GetCapabilityByChatTool(string toolName);
    AgentCapability? GetCapabilityByMcpTool(string toolName);
    bool IsMappedControllerAction(string actionKey);
    string BuildPromptSupplement(AgentContextSnapshot snapshot);
}

public interface IAgentPolicyEvaluator
{
    AgentPolicyDecision Evaluate(AgentPolicyEvaluationContext context);
}

public interface IPendingAgentOperationStore
{
    PendingAgentOperation Create(
        Guid userId,
        AgentCapability capability,
        string operationId,
        string argumentsJson,
        string summary,
        string operationFingerprint,
        AgentExecutionSurface surface);

    PendingAgentOperationConfirmation? Confirm(Guid userId, Guid pendingOperationId);
    PendingAgentOperation? MarkStepUp(Guid userId, Guid pendingOperationId);
    PendingAgentOperationExecution? GetExecution(Guid userId, Guid pendingOperationId);
    bool TryConsumeFreshConfirmation(
        Guid userId,
        string capabilityId,
        string operationFingerprint,
        string confirmationToken,
        bool requireStepUp);
}

public interface IAgentStepUpService
{
    Task<Result<AgentStepUpChallenge>> IssueChallengeAsync(
        Guid userId,
        Guid pendingOperationId,
        string language,
        CancellationToken cancellationToken = default);

    Task<Result<PendingAgentOperation>> VerifyChallengeAsync(
        Guid userId,
        Guid pendingOperationId,
        Guid challengeId,
        string code,
        CancellationToken cancellationToken = default);
}

public interface IAgentAuditService
{
    Task RecordAsync(AgentAuditEntry entry, CancellationToken cancellationToken = default);
}

public interface IAgentTargetOwnershipService
{
    Task<string?> GetDenialReasonAsync(
        string operationId,
        Guid userId,
        JsonElement arguments,
        CancellationToken cancellationToken = default);
}

public interface IAgentOperationExecutor
{
    Task<AgentExecuteOperationResponse> ExecuteAsync(
        AgentExecuteOperationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IDistributedRateLimitService
{
    Task<DistributedRateLimitDecision> TryAcquireAsync(
        string policyName,
        string partitionKey,
        CancellationToken cancellationToken = default);
}

public interface IFeatureFlagService
{
    Task<IReadOnlyList<string>> GetEnabledKeysForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
