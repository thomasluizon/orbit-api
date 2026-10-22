namespace Orbit.Domain.Interfaces;

/// <summary>Authenticates ranking query and page payloads with a dedicated signing purpose.</summary>
public interface IHabitRankingTokenService
{
    string Sign(string payload);
    bool TryRead(string token, out string payload);
}
