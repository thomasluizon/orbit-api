using System.Security.Cryptography;
using System.Text;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Domain.Entities;

public class ApiKey : Entity
{
    public const string ConnectorName = "Claude.ai";

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
    public Guid? OAuthFamilyId { get; private set; }
    public string? OAuthClientId { get; private set; }

    private ApiKey() { }

    public static Result<(ApiKey Entity, string RawKey)> Create(
        Guid userId,
        string name,
        IReadOnlyList<string>? scopes = null,
        bool isReadOnly = false,
        DateTime? expiresAtUtc = null,
        Guid? oauthFamilyId = null,
        string? oauthClientId = null)
    {
        if (userId == Guid.Empty)
            return Result.Failure<(ApiKey, string)>(DomainErrors.UserIdRequired);

        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<(ApiKey, string)>(DomainErrors.ApiKeyNameRequired);

        if (name.Trim().Length > 50)
            return Result.Failure<(ApiKey, string)>(DomainErrors.ApiKeyNameTooLong);

        if (oauthFamilyId is null && IsConnectorName(name))
            return Result.Failure<(ApiKey, string)>(DomainErrors.ApiKeyNameReserved);

        if (expiresAtUtc.HasValue && expiresAtUtc.Value <= DateTime.UtcNow)
            return Result.Failure<(ApiKey, string)>(DomainErrors.ApiKeyExpiryInPast);

        if (oauthFamilyId == Guid.Empty)
            return Result.Failure<(ApiKey, string)>("An OAuth family id must not be empty.");
        if (oauthClientId is not null && (oauthFamilyId is null || string.IsNullOrWhiteSpace(oauthClientId)
            || oauthClientId.Length > OAuthClientMetadata.MaxClientIdLength
            || oauthClientId.Contains(':') && !OAuthClientMetadata.IsMetadataUrl(oauthClientId)))
            return Result.Failure<(ApiKey, string)>("An OAuth client id requires a valid client and key family.");

        var normalizedScopes = (scopes ?? [])
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (scopes is not null && normalizedScopes.Count != scopes.Count)
            return Result.Failure<(ApiKey, string)>(DomainErrors.ApiKeyScopesInvalid);

        var rawKey = GenerateKey();
        var keyHash = BCrypt.Net.BCrypt.HashPassword(rawKey);
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
            IsRevoked = false,
            OAuthFamilyId = oauthFamilyId,
            OAuthClientId = oauthClientId
        };

        return Result.Success((apiKey, rawKey));
    }

    public static bool IsConnectorName(string? name) =>
        string.Equals(name?.Trim(), ConnectorName, StringComparison.OrdinalIgnoreCase);

    public bool Verify(string rawKey)
    {
        if (KeyHash.StartsWith("$2", StringComparison.Ordinal))
            return BCrypt.Net.BCrypt.Verify(rawKey, KeyHash);

        if (KeyHash.Length != 64 || !KeyHash.All(Uri.IsHexDigit))
            return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(KeyHash), SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));
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
