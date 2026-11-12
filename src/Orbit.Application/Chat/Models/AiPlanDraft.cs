namespace Orbit.Application.Chat.Models;

public sealed record AiPlanDraft(string Title, IReadOnlyList<AiPlanDraftStep> Steps);
public sealed record AiPlanDraftStep(string Text, string Tool, string Phase);
