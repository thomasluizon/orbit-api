namespace Orbit.Infrastructure.Configuration;

public sealed class JwtSettings
{
    public const string SectionName = "Jwt";
    public required string SecretKey { get; init; }
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public int ExpiryHours { get; init; } = 168;
    public int ExpiryMinutes { get; init; }
    public int RefreshExpiryDays { get; init; } = 90;

    /// <summary>
    /// Optional secondary signing key used during rotation.
    /// When set, tokens signed with EITHER <see cref="SecretKey"/> or
    /// <see cref="PreviousSecretKey"/> validate successfully. New tokens are
    /// always signed with <see cref="SecretKey"/>. Use the previous key during
    /// the rotation window to keep already-issued tokens valid.
    /// </summary>
    public string? PreviousSecretKey { get; init; }
}
