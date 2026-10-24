using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public sealed class DesktopHandoffStore(OrbitDbContext db) : IDesktopHandoffStore
{
    public async Task AddAsync(DesktopHandoff handoff, CancellationToken cancellationToken) =>
        await db.DesktopHandoffs.AddAsync(handoff, cancellationToken);

    public async Task<Guid?> ConsumeAsync(string codeHash, string state, string codeChallenge, string redirectUri,
        DateTime nowAtUtc, CancellationToken cancellationToken)
    {
        var query = db.DesktopHandoffs.Where(handoff => handoff.CodeHash == codeHash
            && handoff.State == state && handoff.CodeChallenge == codeChallenge
            && handoff.RedirectUri == redirectUri && handoff.ExpiresAtUtc > nowAtUtc);
        var userId = await query.Select(handoff => (Guid?)handoff.UserId).SingleOrDefaultAsync(cancellationToken);
        if (userId is null)
            return null;

        return await query.ExecuteDeleteAsync(cancellationToken) == 1 ? userId : null;
    }
}
