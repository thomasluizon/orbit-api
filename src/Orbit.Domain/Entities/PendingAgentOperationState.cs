using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Domain.Entities;

public class PendingAgentOperationState : Entity
{
    public Guid UserId { get; private set; }
    public string CapabilityId { get; private set; } = null!;
    public string OperationId { get; private set; } = null!;
    public string ArgumentsJson { get; private set; } = "{}";
    public string DisplayName { get; private set; } = null!;
    public string Summary { get; private set; } = null!;
    public string OperationFingerprint { get; private set; } = null!;
    public AgentExecutionSurface Surface { get; private set; }
    public AgentRiskClass RiskClass { get; private set; }
    public AgentConfirmationRequirement ConfirmationRequirement { get; private set; }
    public string? ConfirmationTokenHash { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? ConfirmedAtUtc { get; private set; }
    public DateTime? StepUpSatisfiedAtUtc { get; private set; }
    public DateTime? ConsumedAtUtc { get; private set; }

    private PendingAgentOperationState()
    {
    }

    public static PendingAgentOperationState Create(
        Guid userId,
        AgentCapability capability,
        string operationId,
        string argumentsJson,
        string summary,
        string operationFingerprint,
        AgentExecutionSurface surface,
        DateTime expiresAtUtc)
    {
        return new PendingAgentOperationState
        {
            UserId = userId,
            CapabilityId = capability.Id,
            OperationId = operationId,
            ArgumentsJson = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson,
            DisplayName = capability.DisplayName,
            Summary = summary,
            OperationFingerprint = operationFingerprint,
            Surface = surface,
            RiskClass = capability.RiskClass,
            ConfirmationRequirement = capability.ConfirmationRequirement,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = expiresAtUtc
        };
    }

    public bool IsExpired(DateTime utcNow) => utcNow >= ExpiresAtUtc;

    public bool IsUsable(string capabilityId, string operationFingerprint, bool requireStepUp, DateTime utcNow)
    {
        if (ConsumedAtUtc.HasValue || IsExpired(utcNow))
            return false;

        if (!string.Equals(CapabilityId, capabilityId, StringComparison.Ordinal) ||
            !string.Equals(OperationFingerprint, operationFingerprint, StringComparison.Ordinal))
            return false;

        if (!ConfirmedAtUtc.HasValue)
            return false;

        if (requireStepUp && !StepUpSatisfiedAtUtc.HasValue)
            return false;

        return true;
    }

    public void SetConfirmationTokenHash(string confirmationTokenHash)
    {
        ConfirmationTokenHash = confirmationTokenHash;
        ConfirmedAtUtc = DateTime.UtcNow;
    }

    public void MarkStepUpSatisfied()
    {
        StepUpSatisfiedAtUtc = DateTime.UtcNow;
    }

    public void MarkConsumed()
    {
        ConsumedAtUtc = DateTime.UtcNow;
    }
}
