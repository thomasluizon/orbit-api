namespace Orbit.Infrastructure.Configuration;

public sealed class JwtSettings
{
    public const string SectionName = "Jwt";
    public const int MinSecretKeyLengthBytes = 32; // 256 bits; HMAC-SHA256 minimum
    public required string SecretKey { get; init; }
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public int ExpiryHours { get; init; } = 168;
    public int ExpiryMinutes { get; init; }
    public int? RefreshExpiryDays { get; init; }

    /// <summary>
    /// Throws if the configured secret is too weak for HMAC-SHA256. Call once at startup.
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
    }
}
