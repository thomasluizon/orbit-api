using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IAuthSessionService
{
    Task<Result<SessionTokens>> CreateSessionAsync(Guid userId, string email, CancellationToken cancellationToken = default);
    Task<Result<SessionTokens>> RefreshSessionAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task<Result> RevokeSessionAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task<Result> RevokeAllSessionsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether a stored session exists for the given refresh token. The refresh rate limiter uses
    /// this so only a token that maps to a real, server-issued session earns a per-session partition; a
    /// forged or malformed token an attacker can mint for free never yields a private bucket and is instead
    /// throttled per source IP, closing the token-varying rate-limit bypass.
    /// </summary>
    Task<bool> HasSessionForTokenAsync(string refreshToken, CancellationToken cancellationToken = default);
}
