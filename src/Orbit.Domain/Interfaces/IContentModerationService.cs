namespace Orbit.Domain.Interfaces;

/// <summary>
/// Screens free-text before it is persisted. Implementations MUST NOT throw:
/// a transport/timeout/non-success outcome is surfaced as <see cref="ModerationResult.Unavailable"/>
/// so callers can refuse the input explicitly, while a definitive provider flag sets <see cref="ModerationResult.Flagged"/>.
/// </summary>
// Retained for chat/MCP moderation in ORB-11: https://github.com/thomasluizon/orbit-tickets/issues/17
public interface IContentModerationService
{
    Task<ModerationResult> CheckTextAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of a moderation check. <paramref name="Flagged"/> is a definitive provider rejection;
/// <paramref name="Unavailable"/> means the check could not be completed (callers refuse the input).
/// </summary>
public sealed record ModerationResult(bool Flagged, bool Unavailable, IReadOnlyList<string> Categories);
