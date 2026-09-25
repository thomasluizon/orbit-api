using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class UserSessionRefreshToken : Entity
{
    public Guid UserSessionId { get; private set; }
    public string TokenHash { get; private set; } = null!;

    private UserSessionRefreshToken() { }

    public static Result<UserSessionRefreshToken> Create(Guid userSessionId, string tokenHash)
    {
        if (userSessionId == Guid.Empty)
            return Result.Failure<UserSessionRefreshToken>(DomainErrors.SessionNotActive);

        if (string.IsNullOrWhiteSpace(tokenHash))
            return Result.Failure<UserSessionRefreshToken>(DomainErrors.TokenHashRequired);

        return Result.Success(new UserSessionRefreshToken
        {
            UserSessionId = userSessionId,
            TokenHash = tokenHash
        });
    }
}
