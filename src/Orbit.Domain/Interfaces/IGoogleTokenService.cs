using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

public interface IGoogleTokenService
{
    Task<string?> GetValidAccessTokenAsync(User user, CancellationToken ct = default);
}
