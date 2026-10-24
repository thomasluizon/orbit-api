namespace Orbit.Domain.Interfaces;

public interface IOAuthStateStore
{
    bool Put(string secret, string kind, string payload, DateTime expiresAtUtc, int? capacity = null);
    string? Read(string secret, string kind, DateTime nowAtUtc);
    OAuthStateSnapshot? ReadState(string secret, string kind, DateTime nowAtUtc);
    string? Take(string secret, string kind, DateTime nowAtUtc);
    void RemoveExpired(DateTime nowAtUtc);
}

public sealed record OAuthStateSnapshot(string Payload, bool IsConsumed);
