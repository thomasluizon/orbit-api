namespace Orbit.Application.Common;

/// <summary>
/// Validates the Google-signed OIDC bearer token on a Play RTDN Pub/Sub push request.
/// Returns false for any missing, malformed, or non-push-service-account token, so the
/// webhook fails closed.
/// </summary>
public interface IPlayPushTokenValidator
{
    Task<bool> IsValidAsync(string authorizationHeader);
}
