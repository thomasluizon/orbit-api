namespace Orbit.Application.Common;

public interface IAppleIdentityService
{
    bool IsConfigured { get; }
    Task<AppleIdentity> AuthenticateNativeAsync(string identityToken, string authorizationCode, string rawNonce, CancellationToken cancellationToken);
    Task<AppleIdentity> AuthenticateWebAsync(string authorizationCode, string nonce, string redirectUri, CancellationToken cancellationToken);
    Task RevokeAsync(string refreshToken, string clientId, CancellationToken cancellationToken);
}

public sealed record AppleIdentity(string Subject, string? Email, bool EmailVerified, string RefreshToken, string ClientId);
