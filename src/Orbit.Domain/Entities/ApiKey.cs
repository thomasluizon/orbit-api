using System.Security.Cryptography;
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
            return Result.Failure<(ApiKey, string)>("User ID is required.");

        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<(ApiKey, string)>("API key name is required.");

        if (name.Trim().Length > 50)
            return Result.Failure<(ApiKey, string)>("API key name must be 50 characters or less.");

        if (expiresAtUtc.HasValue && expiresAtUtc.Value <= DateTime.UtcNow)
            return Result.Failure<(ApiKey, string)>("API key expiry must be in the future.");

        var normalizedScopes = (scopes ?? [])
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (scopes is not null && normalizedScopes.Count != scopes.Count)
            return Result.Failure<(ApiKey, string)>("API key scopes must be non-empty strings.");

        var rawKey = GenerateKey();
        var keyHash = BCrypt.Net.BCrypt.HashPassword(rawKey);
        var keyPrefix = rawKey[..12]; // "orb_" + first 8 random chars

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
