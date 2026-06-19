namespace Orbit.Domain.Interfaces;

/// <summary>
/// Posts a critical operational alert to an out-of-band channel (Discord webhook) so the operator
/// learns about a server fault from a push instead of an angry user. The context map carries only
/// non-PII diagnostics (method, path, request id, client ip, user id); implementations must scrub
/// anything sensitive before transmitting. A disabled or failing notifier never throws.
/// </summary>
public interface IAlertNotifier
{
    Task SendCriticalAsync(
        string title,
        string detail,
        IReadOnlyDictionary<string, string?> context,
        CancellationToken cancellationToken);
}
