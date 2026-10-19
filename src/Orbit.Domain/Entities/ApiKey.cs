using System.Security.Cryptography;
using System.Text;
using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class ApiKey : Entity
{
    public Guid UserId { get; private set; }
    public string Name { get; private set; } = null!;
    public string KeyHash { get; private set; } = null!;
    public string KeyPrefix { get; private set; } = null!;
    public IReadOnlyList<string> Scopes { get; private set; } = [];
    public bool IsReadOnly { get; private set; }
    public DateTime? ExpiresAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? LastUsedAtUtc { get; private set; }
    public bool IsRevoked { get; private set; }

    private ApiKey() { }

    public static Result<(ApiKey Entity, string RawKey)> Create(
        Guid userId,
        string name,
        IReadOnlyList<string>? scopes = null,
        bool isReadOnly = false,
        DateTime? expiresAtUtc = null)
    {
        if (userId == Guid.Empty)
            return Result.Failure<(ApiKey, string)>(DomainErrors.UserIdRequired);

        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<(ApiKey, string)>(DomainErrors.ApiKeyNameRequired);

        if (name.Trim().Length > 50)
            return Result.Failure<(ApiKey, string)>(DomainErrors.ApiKeyNameTooLong);

        if (expiresAtUtc.HasValue && expiresAtUtc.Value <= DateTime.UtcNow)
            return Result.Failure<(ApiKey, string)>(DomainErrors.ApiKeyExpiryInPast);

        var normalizedScopes = (scopes ?? [])
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (scopes is not null && normalizedScopes.Count != scopes.Count)
            return Result.Failure<(ApiKey, string)>(DomainErrors.ApiKeyScopesInvalid);

        var rawKey = GenerateKey();
        var keyHash = Hash(rawKey);
        var keyPrefix = rawKey[..12];
        var apiKey = new ApiKey
        {
            UserId = userId,
            Name = name.Trim(),
            KeyHash = keyHash,
            KeyPrefix = keyPrefix,
            Scopes = normalizedScopes,
            IsReadOnly = isReadOnly,
            ExpiresAtUtc = expiresAtUtc,
            CreatedAtUtc = DateTime.UtcNow,
            IsRevoked = false
        };

        return Result.Success((apiKey, rawKey));
    }

    public bool Verify(string rawKey)
    {
        if (KeyHash.Length != 64 || !KeyHash.All(Uri.IsHexDigit))
            return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(KeyHash), SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));
    }

    public void UpgradeHash(string rawKey)
    {
        if (!rawKey.StartsWith(KeyPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Key prefix does not match.", nameof(rawKey));
        KeyHash = Hash(rawKey);
    }

    private static string Hash(string rawKey) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));

    public void Revoke() => IsRevoked = true;

    public void MarkUsed() => LastUsedAtUtc = DateTime.UtcNow;

    public bool HasExpired() => ExpiresAtUtc.HasValue && ExpiresAtUtc.Value <= DateTime.UtcNow;

    private static string GenerateKey()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var randomPart = RandomNumberGenerator.GetString(chars, 32);
        return $"orb_{randomPart}";
    }
}
