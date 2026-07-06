using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IAuthSessionService
{
    Task<Result<SessionTokens>> CreateSessionAsync(Guid userId, string email, CancellationToken cancellationToken = default);
    Task<Result<SessionTokens>> RefreshSessionAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task<Result> RevokeSessionAsync(string refreshToken, CancellationToken cancellationToken = default);
}
