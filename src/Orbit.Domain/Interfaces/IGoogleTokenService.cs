using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

public enum GoogleTokenRefreshResult
{
    Success,
    TransientFailure,
    RefreshTokenInvalid
}

public record GoogleTokenRefreshOutcome(
    string? AccessToken,
    GoogleTokenRefreshResult Result,
    string? ErrorCode);

public static class GoogleTokenErrorCodes
{
    public const string NoRefreshToken = "no_refresh_token";
}

public interface IGoogleTokenService
{
    Task<string?> GetValidAccessTokenAsync(User user, CancellationToken ct = default);

    Task<GoogleTokenRefreshOutcome> TryRefreshAsync(User user, CancellationToken ct = default);
}
