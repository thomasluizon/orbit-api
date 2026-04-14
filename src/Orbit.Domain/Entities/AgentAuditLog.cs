using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Domain.Entities;

public class AgentAuditLog : Entity
{
    private const int CapabilityIdMaxLength = 100;
    private const int SourceNameMaxLength = 100;
    private const int CorrelationIdMaxLength = 100;
    private const int SummaryMaxLength = 500;
    private const int TargetIdMaxLength = 100;
    private const int TargetNameMaxLength = 200;
    private const int RedactedArgumentsMaxLength = 4000;
    private const int ErrorMaxLength = 500;
    private const int ShadowReasonMaxLength = 500;

    public Guid UserId { get; private set; }
    public string CapabilityId { get; private set; } = null!;
    public string SourceName { get; private set; } = null!;
    public AgentExecutionSurface Surface { get; private set; }
    public AgentAuthMethod AuthMethod { get; private set; }
    public AgentRiskClass RiskClass { get; private set; }
    public AgentPolicyDecisionStatus PolicyDecision { get; private set; }
    public AgentOperationStatus OutcomeStatus { get; private set; }
    public string? CorrelationId { get; private set; }
    public string? Summary { get; private set; }
    public string? TargetId { get; private set; }
    public string? TargetName { get; private set; }
    public string? RedactedArguments { get; private set; }
    public string? Error { get; private set; }
    public AgentPolicyDecisionStatus? ShadowPolicyDecision { get; private set; }
    public string? ShadowReason { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private AgentAuditLog()
    {
    }

    public static AgentAuditLog Create(AgentAuditEntry entry)
    {
        return new AgentAuditLog
        {
            UserId = entry.UserId,
            CapabilityId = Truncate(entry.CapabilityId, CapabilityIdMaxLength) ?? string.Empty,
            SourceName = Truncate(entry.SourceName, SourceNameMaxLength) ?? string.Empty,
            Surface = entry.Surface,
            AuthMethod = entry.AuthMethod,
            RiskClass = entry.RiskClass,
            PolicyDecision = entry.PolicyDecision,
            OutcomeStatus = entry.OutcomeStatus,
            CorrelationId = Truncate(entry.CorrelationId, CorrelationIdMaxLength),
            Summary = Truncate(entry.Summary, SummaryMaxLength),
            TargetId = Truncate(entry.TargetId, TargetIdMaxLength),
            TargetName = Truncate(entry.TargetName, TargetNameMaxLength),
            RedactedArguments = Truncate(entry.RedactedArguments, RedactedArgumentsMaxLength),
            Error = Truncate(entry.Error, ErrorMaxLength),
            ShadowPolicyDecision = entry.ShadowPolicyDecision,
            ShadowReason = Truncate(entry.ShadowReason, ShadowReasonMaxLength),
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }
}
