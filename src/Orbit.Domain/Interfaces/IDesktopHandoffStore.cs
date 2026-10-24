using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

public interface IDesktopHandoffStore
{
    Task AddAsync(DesktopHandoff handoff, CancellationToken cancellationToken);
    Task<Guid?> ConsumeAsync(string codeHash, string state, string codeChallenge, string redirectUri,
        DateTime nowAtUtc, CancellationToken cancellationToken);
}
