namespace Orbit.Application.Chat.Models;

/// <summary>
/// Structured "I need to ask the user a question" payload returned by an <c>IAiTool</c>.
/// Surfaces in <c>ActionResult</c> with <c>ActionStatus.NeedsClarification</c>. The frontend
/// renders quick-action buttons; tapping one POSTs the chosen value to
/// <c>POST /api/ai/clarifications/{OperationId}/resolve</c>, which merges the value into
/// the partial arguments stash and re-invokes the original tool deterministically.
/// </summary>
public record ClarificationRequest(
    string Question,
    Guid OperationId,
    string MissingArgumentKey,
    IReadOnlyList<QuickAction> QuickActions);
