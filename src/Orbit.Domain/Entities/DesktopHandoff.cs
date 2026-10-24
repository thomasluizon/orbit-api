using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public sealed class DesktopHandoff : Entity
{
    public const int LifetimeSeconds = 60;
    public const int MaxStateLength = 512;
    public const string InvalidHandoff = "Invalid or expired desktop handoff.";

    public Guid UserId { get; private set; }
    public string CodeHash { get; private set; } = string.Empty;
    public string State { get; private set; } = string.Empty;
    public string CodeChallenge { get; private set; } = string.Empty;
    public string RedirectUri { get; private set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; private set; }

    private DesktopHandoff() { }

    public static Result<DesktopHandoff> Create(
        Guid userId, string codeHash, string state, string codeChallenge,
        string codeChallengeMethod, string redirectUri, string allowedRedirectUri, DateTime issuedAtUtc)
    {
        if (userId == Guid.Empty || !IsValidCode(codeHash) || !IsValidState(state)
            || !IsValidChallenge(codeChallenge) || codeChallengeMethod != "S256"
            || !IsAllowedRedirect(redirectUri, allowedRedirectUri) || issuedAtUtc.Kind != DateTimeKind.Utc)
            return Result.Failure<DesktopHandoff>(InvalidHandoff);

        return Result.Success(new DesktopHandoff
        {
            UserId = userId,
            CodeHash = codeHash,
            State = state,
            CodeChallenge = codeChallenge,
            RedirectUri = redirectUri,
            ExpiresAtUtc = issuedAtUtc.AddSeconds(LifetimeSeconds)
        });
    }

    public static bool IsValidState(string? state) =>
        !string.IsNullOrWhiteSpace(state) && state.Length <= MaxStateLength;

    public static bool IsValidCode(string? code) =>
        code is { Length: 64 } && code.All(Uri.IsHexDigit);

    public static bool IsValidChallenge(string? challenge) =>
        challenge is { Length: 43 } && challenge.All(IsBase64UrlCharacter);

    public static bool IsValidVerifier(string? verifier) =>
        verifier is { Length: >= 43 and <= 128 }
        && verifier.All(character => IsBase64UrlCharacter(character) || character is '.' or '~');

    public static bool IsAllowedRedirect(string? redirectUri, string? allowedRedirectUri) =>
        !string.IsNullOrWhiteSpace(allowedRedirectUri)
        && allowedRedirectUri.Length <= 2048
        && Uri.IsWellFormedUriString(allowedRedirectUri, UriKind.Absolute)
        && string.Equals(redirectUri, allowedRedirectUri, StringComparison.Ordinal);

    private static bool IsBase64UrlCharacter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
}
