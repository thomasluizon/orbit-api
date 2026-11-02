namespace Orbit.Application.Chat.Models;

public sealed record AstraPlanDraft(string Title, IReadOnlyList<AstraPlanDraftStep> Steps);
public sealed record AstraPlanDraftStep(string Text, string Tool, string Phase);
