using Google.Apis.Auth;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Google-backed <see cref="IPlayPushTokenValidator"/>: verifies the Pub/Sub push OIDC token's
/// signature and audience, then confirms its issuer and that it was minted by the configured
/// push service account before trusting the RTDN payload.
/// </summary>
public sealed class GooglePlayPushTokenValidator(IOptions<GooglePlaySettings> settings) : IPlayPushTokenValidator
{
    private readonly GooglePlaySettings _settings = settings.Value;

    /// <summary>
    /// Issuers Google emits on push OIDC tokens. Google mints <c>https://accounts.google.com</c>
    /// but documents both forms as acceptable, so we honour both.
    /// https://cloud.google.com/pubsub/docs/authenticate-push-subscriptions
    /// </summary>
    private static readonly string[] AcceptedIssuers =
        ["https://accounts.google.com", "accounts.google.com"];

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
            return IsAuthenticatedRtdnSender(payload, _settings);
        }
        catch (InvalidJwtException)
        {
            return false;
        }
    }

    /// <summary>
    /// Confirms a signature-verified payload belongs to the configured Play RTDN sender: a Google
    /// issuer, a verified email, and the exact push service-account address. The trust boundary lives
    /// here so the sender contract is explicit and regression-tested rather than implicit in the JWT
    /// library's generic Google-issuer allowlist.
    /// </summary>
    internal static bool IsAuthenticatedRtdnSender(GoogleJsonWebSignature.Payload payload, GooglePlaySettings settings) =>
        payload.Issuer is not null
        && AcceptedIssuers.Contains(payload.Issuer, StringComparer.Ordinal)
        && payload.EmailVerified
        && string.Equals(payload.Email, settings.RtdnServiceAccountEmail, StringComparison.OrdinalIgnoreCase);
}
