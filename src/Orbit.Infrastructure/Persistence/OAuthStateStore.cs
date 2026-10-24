using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public sealed class OAuthStateStore(OrbitDbContext db, TimeProvider timeProvider) : IOAuthStateStore
{
    public bool Put(string secret, string kind, string payload, DateTime expiresAtUtc, int? capacity = null)
    {
        return VerifiedTransaction.Execute(db, PutCore);

        bool PutCore()
        {
            if (capacity.HasValue)
            {
                if (db.Database.IsNpgsql())
                    db.Database.ExecuteSqlRaw("SELECT pg_advisory_xact_lock(hashtext({0}))", $"oauth-capacity:{kind}");
                var nowAtUtc = timeProvider.GetUtcNow().UtcDateTime;
                if (db.OAuthStates.Count(state => state.Kind == kind && state.ExpiresAtUtc > nowAtUtc) >= capacity.Value)
                    return false;
            }
            var state = OAuthState.Create(Hash(secret), kind, payload, expiresAtUtc);
            db.OAuthStates.Add(state);
            try
            {
                db.SaveChanges();
                return true;
            }
            finally
            {
                db.Entry(state).State = EntityState.Detached;
            }
        }
    }

    public string? Read(string secret, string kind, DateTime nowAtUtc) => ReadState(secret, kind, nowAtUtc)?.Payload;

    public OAuthStateSnapshot? ReadState(string secret, string kind, DateTime nowAtUtc)
    {
        var hash = Hash(secret);
        return db.OAuthStates.AsNoTracking().Where(state => state.SecretHash == hash && state.Kind == kind && state.ExpiresAtUtc > nowAtUtc)
            .Select(state => new OAuthStateSnapshot(state.Payload, state.ConsumedAtUtc != null)).SingleOrDefault();
    }

    public string? Take(string secret, string kind, DateTime nowAtUtc) => VerifiedTransaction.Execute(db, () =>
    {
        var hash = Hash(secret);
        var payload = Read(secret, kind, nowAtUtc);
        if (payload is null)
            return null;
        var query = db.OAuthStates.Where(state => state.SecretHash == hash && state.Kind == kind && state.ExpiresAtUtc > nowAtUtc);
        var affected = kind is "refresh" or "consent"
            ? query.Where(state => state.ConsumedAtUtc == null)
                .ExecuteUpdate(setters => setters.SetProperty(state => state.ConsumedAtUtc, nowAtUtc))
            : query.ExecuteDelete();
        return affected == 1 ? payload : null;
    });

    public void RemoveExpired(DateTime nowAtUtc) => db.OAuthStates.Where(state => state.ExpiresAtUtc <= nowAtUtc).ExecuteDelete();

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
