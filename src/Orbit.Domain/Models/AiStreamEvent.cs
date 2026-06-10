namespace Orbit.Domain.Models;

public enum AiStreamEventKind
{
    Delta,
    Reset
}

/// <summary>
/// Incremental output emitted while an AI completion streams. Delta carries a text
/// fragment of the answer; Reset tells the consumer to discard text emitted so far
/// because the round turned out to be a tool-call round.
/// </summary>
public sealed record AiStreamEvent(AiStreamEventKind Kind, string? Text)
{
    public static AiStreamEvent Delta(string text) => new(AiStreamEventKind.Delta, text);
    public static AiStreamEvent Reset() => new(AiStreamEventKind.Reset, null);
}
