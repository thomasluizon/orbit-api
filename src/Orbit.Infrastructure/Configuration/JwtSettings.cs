namespace Orbit.Infrastructure.Configuration;

public sealed class JwtSettings
{
    public const string SectionName = "Jwt";
    public const int MinSecretKeyLengthBytes = 32;
    public const int MaxAccessTokenLifetimeMinutes = 60;
    public required string SecretKey { get; init; }
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public int ExpiryHours { get; init; }
    public int ExpiryMinutes { get; init; }
    public int? RefreshExpiryDays { get; init; }

    /// <summary>
    /// Throws if the secret is too weak for HMAC-SHA256 or the access-token lifetime is unsafe. Call once at startup.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SecretKey))
            throw new InvalidOperationException($"Jwt:SecretKey is required.");
        var bytes = System.Text.Encoding.UTF8.GetByteCount(SecretKey);
        if (bytes < MinSecretKeyLengthBytes)
            throw new InvalidOperationException(
                $"Jwt:SecretKey must be at least {MinSecretKeyLengthBytes} bytes ({bytes} provided). " +
                $"Use a 32-byte cryptographically random value for HMAC-SHA256.");

        var lifetimeMinutes = ExpiryMinutes > 0 ? (long)ExpiryMinutes : (long)ExpiryHours * 60;
        if (lifetimeMinutes <= 0 || lifetimeMinutes > MaxAccessTokenLifetimeMinutes)
            throw new InvalidOperationException(
                $"Jwt:ExpiryMinutes ({ExpiryMinutes}) and Jwt:ExpiryHours ({ExpiryHours}) must resolve to an " +
                $"access-token lifetime greater than 0 and at most {MaxAccessTokenLifetimeMinutes} minutes " +
                $"({lifetimeMinutes} minutes configured).");
    }
}
