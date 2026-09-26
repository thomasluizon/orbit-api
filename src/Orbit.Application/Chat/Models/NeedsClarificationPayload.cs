namespace Orbit.Application.Chat.Models;

public record NeedsClarificationPayload(
    string Question,
    string MissingArgumentKey,
    IReadOnlyList<QuickAction>? QuickActions);
