namespace Orbit.Domain.Interfaces;

/// <summary>
/// Mints and verifies the self-contained token embedded in the waitlist
/// confirmation link. The token carries the normalized email and language and is
/// signed so the confirmation endpoint needs no server-side pending-signup store.
/// </summary>
public interface IWaitlistConfirmationTokenService
{
    string CreateToken(string email, string language);

    bool TryValidateToken(string token, out string email, out string language);
}
