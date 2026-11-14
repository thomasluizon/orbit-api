using System.Text.Json;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Domain.Entities;

public class PendingAgentOperationState : Entity
{
    public const int CurrentArgumentSchemaVersion = 1;
    public const int MaxExecutionJsonLength = 16_000;
    /// <summary>
    /// The largest stored approved preview. It holds the smallest preview tier of a bulk batch of 100 items at the
    /// longest habit title: each item keeps its id, its entity id, its name and its title field.
    /// </summary>
    public const int MaxRevisedPreviewJsonLength = 160_000;

    public int ArgumentSchemaVersion { get; private set; }
    public Guid UserId { get; private set; }
    public Guid? ConversationId { get; private set; }
    public string CapabilityId { get; private set; } = null!;
    public string OperationId { get; private set; } = null!;
    public string ArgumentsJson { get; private set; } = "{}";
    public string DisplayName { get; private set; } = null!;
    public string Summary { get; private set; } = null!;
    public string OperationFingerprint { get; private set; } = null!;
    public string? PreviewFingerprint { get; private set; }
    public int RevisionNumber { get; private set; }
    public AgentExecutionSurface Surface { get; private set; }
    public AgentRiskClass RiskClass { get; private set; }
    public AgentConfirmationRequirement ConfirmationRequirement { get; private set; }
    public string? ConfirmationTokenHash { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? ConfirmedAtUtc { get; private set; }
    public DateTime? StepUpSatisfiedAtUtc { get; private set; }
    public DateTime? ConsumedAtUtc { get; private set; }
    public string? ExecutionJson { get; private set; }
    public string? RevisedPreviewJson { get; private set; }

    /// <summary>
    /// True when the person revised or refreshed the preview before approval. Null on an operation stored before
    /// this flag existed, because its revisions are unknown.
    /// </summary>
    public bool? PreviewRevised { get; private set; }

    private PendingAgentOperationState() { }

    public static PendingAgentOperationState Create(PendingAgentOperationStateCreateRequest request)
    {
        return new PendingAgentOperationState
        {
            ArgumentSchemaVersion = CurrentArgumentSchemaVersion,
            UserId = request.UserId,
            ConversationId = request.Conversation?.Id,
            CapabilityId = request.Capability.Id,
            OperationId = request.OperationId,
            ArgumentsJson = string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson,
            DisplayName = request.Capability.DisplayName,
            Summary = request.Summary,
            OperationFingerprint = request.OperationFingerprint,
            Surface = request.Surface,
            RiskClass = request.Capability.RiskClass,
            ConfirmationRequirement = request.Capability.ConfirmationRequirement,
            CreatedAtUtc = request.Conversation?.StartedAtUtc ?? DateTime.UtcNow,
            ExpiresAtUtc = request.ExpiresAtUtc,
            PreviewRevised = false
        };
    }

    public bool IsExpired(DateTime utcNow) => utcNow >= ExpiresAtUtc;

    public bool IsUsable(string capabilityId, string operationFingerprint, bool requireStepUp, DateTime utcNow)
    {
        if (ArgumentSchemaVersion != CurrentArgumentSchemaVersion || ConsumedAtUtc.HasValue || IsExpired(utcNow))
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
        RevisionNumber++;
    }

    public void MarkStepUpSatisfied()
    {
        StepUpSatisfiedAtUtc = DateTime.UtcNow;
    }

    public void MarkConsumed()
    {
        ConsumedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Keeps the operation result that the approved execution returned, so a reopened conversation can show it.
    /// </summary>
    public bool RecordExecution(string executionJson)
    {
        if (!ConfirmedAtUtc.HasValue || ConfirmationTokenHash is null || string.IsNullOrWhiteSpace(executionJson)
            || executionJson.Length > MaxExecutionJsonLength || !IsJsonObject(executionJson))
            return false;
        ExecutionJson = executionJson;
        return true;
    }

    private static bool IsStorablePreview(string? previewJson) =>
        !string.IsNullOrWhiteSpace(previewJson) && previewJson.Length <= MaxRevisedPreviewJsonLength && IsJsonObject(previewJson);

    private static bool IsJsonObject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Binds new arguments and a new preview fingerprint. A valid <paramref name="revisedPreviewJson"/> keeps the preview
    /// that the person saw, so a reopened conversation shows the approved words. A null or invalid value keeps the stored
    /// preview on the same preview fingerprint and clears it on a new one. <paramref name="revisedByPerson"/> marks a
    /// revision or a refresh that the person asked for, also when the preview is too large to keep.
    /// </summary>
    public bool Revise(string argumentsJson, string operationFingerprint, string previewFingerprint, DateTime utcNow,
        string? revisedPreviewJson = null, bool revisedByPerson = false)
    {
        if (IsExpired(utcNow) || ConsumedAtUtc.HasValue
            || string.IsNullOrWhiteSpace(argumentsJson)
            || string.IsNullOrWhiteSpace(operationFingerprint)
            || string.IsNullOrWhiteSpace(previewFingerprint)
            || previewFingerprint.Length > 256
            || AgentOperationFingerprint.Compute(OperationId, argumentsJson) != operationFingerprint)
            return false;

        if (IsStorablePreview(revisedPreviewJson))
            RevisedPreviewJson = revisedPreviewJson;
        else if (!string.Equals(PreviewFingerprint, previewFingerprint, StringComparison.Ordinal))
            RevisedPreviewJson = null;
        ArgumentsJson = argumentsJson;
        OperationFingerprint = operationFingerprint;
        PreviewFingerprint = previewFingerprint;
        ConfirmationTokenHash = null;
        ConfirmedAtUtc = null;
        StepUpSatisfiedAtUtc = null;
        ExecutionJson = null;
        if (revisedByPerson)
            PreviewRevised = true;
        RevisionNumber++;
        return true;
    }

    public PendingAgentOperationRevision CurrentRevision() =>
        new(ArgumentsJson, OperationFingerprint, PreviewFingerprint, RevisedPreviewJson, PreviewRevised, RevisionNumber);

    /// <summary>
    /// Puts back the state from before a revision, the revision number included, so the person approves the earlier
    /// arguments and a reopened conversation shows the earlier outcome. An approved, used or expired operation stays
    /// as it is.
    /// </summary>
    public bool Restore(PendingAgentOperationRevision revision, DateTime utcNow)
    {
        if (IsExpired(utcNow) || ConsumedAtUtc.HasValue || ConfirmedAtUtc.HasValue
            || AgentOperationFingerprint.Compute(OperationId, revision.ArgumentsJson) != revision.OperationFingerprint)
            return false;
        ArgumentsJson = revision.ArgumentsJson;
        OperationFingerprint = revision.OperationFingerprint;
        PreviewFingerprint = revision.PreviewFingerprint;
        RevisedPreviewJson = revision.RevisedPreviewJson;
        PreviewRevised = revision.PreviewRevised;
        RevisionNumber = revision.RevisionNumber;
        return true;
    }

    public bool Cancel(DateTime utcNow)
    {
        if (IsExpired(utcNow) || ConsumedAtUtc.HasValue)
            return false;
        return CancelForAccountCleanup(UserId, utcNow);
    }

    public bool CancelForAccountCleanup(Guid userId, DateTime cancelledAtUtc)
    {
        if (userId == Guid.Empty || userId != UserId || ConsumedAtUtc.HasValue)
            return false;
        ConsumedAtUtc = cancelledAtUtc;
        ConfirmationTokenHash = null;
        RevisionNumber++;
        return true;
    }
}

public sealed class PendingAgentOperationStateCreateRequest
{
    public required Guid UserId { get; init; }
    public required AgentCapability Capability { get; init; }
    public required string OperationId { get; init; }
    public required string ArgumentsJson { get; init; }
    public required string Summary { get; init; }
    public required string OperationFingerprint { get; init; }
    public required AgentExecutionSurface Surface { get; init; }
    public required DateTime ExpiresAtUtc { get; init; }
    public PendingOperationConversation? Conversation { get; init; }
}
