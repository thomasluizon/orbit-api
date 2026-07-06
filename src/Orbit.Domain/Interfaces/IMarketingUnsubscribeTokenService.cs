namespace Orbit.Domain.Interfaces;

/// <summary>
/// Mints and verifies the signed, self-contained token embedded in the one-click unsubscribe
/// link of every marketing email. The token carries only the user id, protected with ASP.NET
/// Core Data Protection under a dedicated purpose, so the public unsubscribe endpoint needs no
/// login and no server-side lookup, and a token minted for any other purpose fails to validate.
/// </summary>
public interface IMarketingUnsubscribeTokenService
{
    string CreateToken(Guid userId);

    bool TryValidateToken(string token, out Guid userId);
}
