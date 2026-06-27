namespace Orbit.Domain.Interfaces;

/// <summary>
/// Screens free-text (currently cheer notes) before it is persisted. Implementations MUST NOT throw:
/// a transport/timeout/non-success outcome is surfaced as <see cref="ModerationResult.Unavailable"/>
/// so callers can fail open, while a definitive provider flag sets <see cref="ModerationResult.Flagged"/>.
/// </summary>
public interface IContentModerationService
{
    Task<ModerationResult> CheckTextAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of a moderation check. <paramref name="Flagged"/> is a definitive provider rejection;
/// <paramref name="Unavailable"/> means the check could not be completed (callers fail open).
/// </summary>
public sealed record ModerationResult(bool Flagged, bool Unavailable, IReadOnlyList<string> Categories);
