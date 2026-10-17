using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public sealed class NotionOAuthState : Entity
{
    public Guid UserId { get; private set; }
    public string StateHash { get; private set; } = string.Empty;
    public string RedirectUri { get; private set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; private set; }
    public bool IsConsumed { get; private set; }

    private NotionOAuthState() { }

    public static NotionOAuthState Create(Guid userId, string stateHash, string redirectUri, DateTime issuedAtUtc)
    {
        if (userId == Guid.Empty) throw new ArgumentException("A user is required.", nameof(userId));
        if (stateHash is null || stateHash.Length != 64 || !stateHash.All(Uri.IsHexDigit))
            throw new ArgumentException("A SHA-256 state hash is required.", nameof(stateHash));
        if (!Uri.IsWellFormedUriString(redirectUri, UriKind.Absolute))
            throw new ArgumentException("An absolute redirect URI is required.", nameof(redirectUri));
        if (issuedAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("A UTC timestamp is required.");
        return new NotionOAuthState
        {
            UserId = userId,
            StateHash = stateHash,
            RedirectUri = redirectUri,
            ExpiresAtUtc = issuedAtUtc.AddMinutes(10)
        };
    }
}
