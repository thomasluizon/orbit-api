namespace Orbit.Domain.Models;

public record AiActionPlan
{
    public required IReadOnlyList<AiAction> Actions { get; init; }
    public string? AiMessage { get; init; }
}
