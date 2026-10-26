namespace Orbit.Domain.Interfaces;

public sealed record TurnstileVerificationResult(bool Success, string[] ErrorCodes, string? Hostname = null);

public interface ITurnstileVerificationService
{
    Task<TurnstileVerificationResult> VerifyAsync(
        string token,
        CancellationToken cancellationToken = default);
}
