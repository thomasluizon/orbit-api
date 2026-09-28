namespace Orbit.Domain.Interfaces;

/// <summary>
/// Mints and verifies the signed, self-contained token embedded in the one-click unsubscribe
/// link of every marketing email. The token carries a user or waitlist contact id and is signed
/// under the marketing purpose. The public unsubscribe endpoint resolves the id without a login.
/// </summary>
public interface IMarketingUnsubscribeTokenService
{
    string CreateToken(Guid userId);

    bool TryValidateToken(string token, out Guid userId);
}
