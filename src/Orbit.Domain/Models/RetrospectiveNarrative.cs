namespace Orbit.Domain.Models;

/// <summary>
/// The four plain-text sections of an AI-generated retrospective. The AI service emits a single
/// labeled document which is parsed into these fields; on a parse miss the whole text lands in
/// <see cref="Highlights"/> and the rest are empty.
/// </summary>
public record RetrospectiveNarrative(
    string Highlights,
    string Missed,
    string Trends,
    string Suggestion);
