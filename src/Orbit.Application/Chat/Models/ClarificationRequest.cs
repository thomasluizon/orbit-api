namespace Orbit.Application.Chat.Models;

/// <summary>
/// External-facing "I need to ask the user a question" payload. The frontend renders
/// quick-action buttons; tapping one POSTs the chosen <c>QuickAction.Value</c> to
/// <c>POST /api/ai/clarifications/{OperationId}/resolve</c>, which merges the value
/// into the partial arguments stash and re-invokes the original tool deterministically.
/// <para>
/// Tools do not construct this directly — they return a <see cref="NeedsClarificationPayload"/>
/// and the chat handler attaches the store-minted <c>OperationId</c> when building the
/// outbound <c>ActionResult</c>.
/// </para>
/// </summary>
public record ClarificationRequest(
    string Question,
    Guid OperationId,
    string MissingArgumentKey,
    IReadOnlyList<QuickAction> QuickActions);
