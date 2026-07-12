using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class UserSession : Entity
{
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTime? ExpiresAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime LastUsedAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }

    private UserSession() { }

    public static Result<UserSession> Create(Guid userId, string tokenHash, DateTime? expiresAtUtc)
    {
        if (userId == Guid.Empty)
            return Result.Failure<UserSession>(DomainErrors.UserIdRequired);

        if (string.IsNullOrWhiteSpace(tokenHash))
            return Result.Failure<UserSession>(DomainErrors.TokenHashRequired);

        return Result.Success(new UserSession
        {
            UserId = userId,
            TokenHash = tokenHash,
            ExpiresAtUtc = expiresAtUtc,
            CreatedAtUtc = DateTime.UtcNow,
            LastUsedAtUtc = DateTime.UtcNow
        });
    }

    public bool CanUse(DateTime nowUtc) =>
        RevokedAtUtc is null &&
        (!ExpiresAtUtc.HasValue || ExpiresAtUtc.Value > nowUtc);

    public Result Rotate(string newTokenHash, DateTime? newExpiresAtUtc, DateTime usedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(newTokenHash))
            return Result.Failure(DomainErrors.TokenHashRequired);

        if (!CanUse(usedAtUtc))
            return Result.Failure(DomainErrors.SessionNotActive);

        TokenHash = newTokenHash;
        ExpiresAtUtc = newExpiresAtUtc;
        LastUsedAtUtc = usedAtUtc;
        return Result.Success();
    }

    public void Revoke(DateTime revokedAtUtc)
    {
        RevokedAtUtc ??= revokedAtUtc;
    }
}
