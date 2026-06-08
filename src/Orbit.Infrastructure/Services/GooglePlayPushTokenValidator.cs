using Google.Apis.Auth;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Google-backed <see cref="IPlayPushTokenValidator"/>: verifies the Pub/Sub push OIDC token's
/// signature and audience, and confirms it was minted by the configured push service account.
/// </summary>
public sealed class GooglePlayPushTokenValidator(IOptions<GooglePlaySettings> settings) : IPlayPushTokenValidator
{
    private readonly GooglePlaySettings _settings = settings.Value;

    public async Task<bool> IsValidAsync(string authorizationHeader)
    {
        const string bearerPrefix = "Bearer ";
        if (!authorizationHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var payload = await GoogleJsonWebSignature.ValidateAsync(
                authorizationHeader[bearerPrefix.Length..],
                new GoogleJsonWebSignature.ValidationSettings { Audience = [_settings.RtdnAudience] });
            return payload.EmailVerified
                && string.Equals(payload.Email, _settings.RtdnServiceAccountEmail, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidJwtException)
        {
            return false;
        }
    }
}
