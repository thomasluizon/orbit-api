using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public sealed class OAuthState : Entity
{
    public string SecretHash { get; private set; } = string.Empty;
    public string Kind { get; private set; } = string.Empty;
    public string? PartitionKey { get; private set; }
    public string Payload { get; private set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? ConsumedAtUtc { get; private set; }

    private OAuthState() { }

    public static OAuthState Create(string secretHash, string kind, string payload, DateTime expiresAtUtc, string? partitionKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        if (secretHash.Length != 64 || !secretHash.All(Uri.IsHexDigit))
            throw new ArgumentException("A SHA-256 hash is required.", nameof(secretHash));
        if (kind.Length > 30)
            throw new ArgumentException("OAuth state kind is too long.", nameof(kind));
        if (expiresAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("A UTC expiry is required.", nameof(expiresAtUtc));
        if (partitionKey?.Length > 64)
            throw new ArgumentException("OAuth partition key is too long.", nameof(partitionKey));
        return new OAuthState { PartitionKey = partitionKey, SecretHash = secretHash, Kind = kind, Payload = payload, ExpiresAtUtc = expiresAtUtc };
    }
}
