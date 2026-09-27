using Orbit.Domain.Common;

namespace Orbit.Domain.Interfaces;

public sealed record GoogleCodeIdentity(string Email, string Name, string? AccessToken, string? RefreshToken);

public interface IGoogleAuthorizationCodeService
{
    Task<Result<GoogleCodeIdentity>> ExchangeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken);
}
