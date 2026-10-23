namespace Orbit.Domain.Entities;

public partial class User
{
    public string? AppleSubject { get; private set; }
    public string? AppleRefreshToken { get; private set; }
    public string? AppleTokenClientId { get; private set; }

    public void SetAppleIdentity(string subject, string refreshToken, string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        if (subject.Length > 255 || clientId.Length > 255 || refreshToken.Length > 16_384)
            throw new ArgumentException("Apple credentials exceed the supported length.");
        if (AppleSubject is not null && AppleSubject != subject)
            throw new InvalidOperationException("An account cannot replace its Apple identity.");
        AppleSubject = subject;
        AppleRefreshToken = refreshToken;
        AppleTokenClientId = clientId;
    }

    public void ClearRevokedAppleToken(string refreshToken)
    {
        if (!IsDeactivated || AppleRefreshToken != refreshToken)
            throw new InvalidOperationException("Revocation must match a deactivated account's Apple token.");
        AppleRefreshToken = null;
        AppleTokenClientId = null;
    }
}
