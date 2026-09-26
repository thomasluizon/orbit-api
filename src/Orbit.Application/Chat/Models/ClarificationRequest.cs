namespace Orbit.Application.Chat.Models;

public record ClarificationRequest(
    string Question,
    Guid OperationId,
    string MissingArgumentKey,
    IReadOnlyList<QuickAction> QuickActions);
