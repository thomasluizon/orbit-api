namespace Orbit.Infrastructure.Configuration;

public sealed class EncryptionSettings
{
    public const string SectionName = "Encryption";
    public required string Key { get; init; }       // Base64-encoded 256-bit AES key
}
