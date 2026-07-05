namespace Orbit.Domain.Interfaces;

public enum ResendWebhookVerification
{
    Verified,
    SecretNotConfigured,
    InvalidSignature
}

/// <summary>
/// Verifies the Svix signature Resend attaches to every webhook delivery. Implemented in
/// Infrastructure (which owns the Svix dependency and the signing secret) so the Application
/// webhook handler stays free of both.
/// </summary>
public interface IResendWebhookVerifier
{
    ResendWebhookVerification Verify(string payload, string svixId, string svixTimestamp, string svixSignature);
}
