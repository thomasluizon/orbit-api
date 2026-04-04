using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class UserSession : Entity
{
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime LastUsedAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }

    private UserSession() { }

    public static Result<UserSession> Create(Guid userId, string tokenHash, DateTime expiresAtUtc)
    {
        if (userId == Guid.Empty)
            return Result.Failure<UserSession>("User ID is required.");

        if (string.IsNullOrWhiteSpace(tokenHash))
            return Result.Failure<UserSession>("Token hash is required.");

        return Result.Success(new UserSession
        {
            UserId = userId,
            TokenHash = tokenHash,
            ExpiresAtUtc = expiresAtUtc,
            CreatedAtUtc = DateTime.UtcNow,
            LastUsedAtUtc = DateTime.UtcNow
        });
    }

    public bool CanUse(DateTime nowUtc) => RevokedAtUtc is null && ExpiresAtUtc > nowUtc;

    public void Rotate(string newTokenHash, DateTime newExpiresAtUtc, DateTime usedAtUtc)
    {
        TokenHash = newTokenHash;
        ExpiresAtUtc = newExpiresAtUtc;
        LastUsedAtUtc = usedAtUtc;
    }

    public void Revoke(DateTime revokedAtUtc)
    {
        RevokedAtUtc ??= revokedAtUtc;
    }
}
