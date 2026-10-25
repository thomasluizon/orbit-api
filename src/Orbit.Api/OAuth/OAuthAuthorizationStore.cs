using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orbit.Domain.Interfaces;

namespace Orbit.Api.OAuth;

public sealed class OAuthAuthorizationStore(IOAuthStateStore states, TimeProvider timeProvider)
{
    private static readonly TimeSpan CodeExpiry = TimeSpan.FromMinutes(5);
    internal const int MaxPendingGoogleRequests = 1_000;

    public GoogleAuthorizationRequest? TryCreateGoogleRequest(string clientId, string redirectUri, string clientState,
        string codeChallenge, string? nonce, string googleRedirectUri, string language, IReadOnlyList<string>? scopes = null)
    {
        Cleanup();
        var verifier = NewSecret();
        var challenge = Challenge(verifier);
        var request = new GoogleAuthorizationRequest(NewSecret(), clientId, redirectUri, clientState,
            codeChallenge, nonce, googleRedirectUri, language, verifier, challenge, timeProvider.GetUtcNow(), scopes);
        return states.Put(request.GoogleState, "google", JsonSerializer.Serialize(request),
            timeProvider.GetUtcNow().UtcDateTime.Add(CodeExpiry), MaxPendingGoogleRequests) ? request : null;
    }

    public GoogleAuthorizationRequest? ConsumeGoogleRequest(string state) =>
        Deserialize<GoogleAuthorizationRequest>(states.Take(state, "google", timeProvider.GetUtcNow().UtcDateTime));

    public string CreateCode(Guid userId, string codeChallenge, string redirectUri, string clientId, string? nonce = null,
        IReadOnlyList<string>? scopes = null, bool isReadOnly = false)
    {
        Cleanup();
        var code = NewSecret();
#pragma warning disable ORBIT0004 // CreateCode stamps AuthorizationEntry.CreatedAt for the authorization code TTL, independently of the user timezone. https://github.com/thomasluizon/orbit-tickets/issues/230
        var entry = new AuthorizationEntry(userId, codeChallenge, redirectUri, clientId, nonce, DateTime.UtcNow, scopes, isReadOnly);
#pragma warning restore ORBIT0004
        states.Put(code, "code", JsonSerializer.Serialize(entry), timeProvider.GetUtcNow().UtcDateTime.Add(CodeExpiry));
        return code;
    }

    public AuthorizationEntry? ExchangeCode(string code, string codeVerifier, string redirectUri)
    {
        var entry = Deserialize<AuthorizationEntry>(states.Take(code, "code", timeProvider.GetUtcNow().UtcDateTime));
        if (entry is null)
            return null;
#pragma warning disable ORBIT0004 // ExchangeCode compares elapsed time since AuthorizationEntry.CreatedAt with CodeExpiry before accepting a code. https://github.com/thomasluizon/orbit-tickets/issues/230
        if (DateTime.UtcNow - entry.CreatedAt > CodeExpiry)
#pragma warning restore ORBIT0004
            return null;
        return entry.RedirectUri == redirectUri && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Challenge(codeVerifier)), Encoding.ASCII.GetBytes(entry.CodeChallenge)) ? entry : null;
    }

    public string CreatePendingConsent(ConsentEntry entry) => Put("consent", entry, CodeExpiry);
    public ConsentTicket? ReadPendingConsent(string secret)
    {
        var state = states.ReadState(secret, "consent", timeProvider.GetUtcNow().UtcDateTime);
        if (state is null)
            return null;
        var entry = Deserialize<ConsentEntry>(state.Payload);
        return entry is null ? null : new ConsentTicket(entry, state.IsConsumed);
    }

    public ConsentTicket? ConsumePendingConsent(string secret)
    {
        var entry = Deserialize<ConsentEntry>(states.Take(secret, "consent", timeProvider.GetUtcNow().UtcDateTime));
        if (entry is not null)
            return new ConsentTicket(entry, false);
        return ReadPendingConsent(secret) is { IsConsumed: true } used ? used : null;
    }
    public string CreateRefreshToken(RefreshEntry entry)
    {
        var expiresAtUtc = entry.FamilyExpiresAtUtc ?? timeProvider.GetUtcNow().UtcDateTime.AddDays(30);
        entry = entry with { FamilyId = entry.FamilyId == Guid.Empty ? entry.ApiKeyId : entry.FamilyId, FamilyExpiresAtUtc = expiresAtUtc };
        return PutUntil("refresh", entry, expiresAtUtc);
    }

    public RefreshEntry? ReadRefreshToken(string secret)
    {
        var entry = Deserialize<RefreshEntry>(states.Read(secret, "refresh", timeProvider.GetUtcNow().UtcDateTime));
        return entry is null ? null : entry with { FamilyId = entry.FamilyId == Guid.Empty ? entry.ApiKeyId : entry.FamilyId };
    }
    public RefreshEntry? ConsumeRefreshToken(string secret) => Deserialize<RefreshEntry>(states.Take(secret, "refresh", timeProvider.GetUtcNow().UtcDateTime));

    private string Put<T>(string kind, T entry, TimeSpan lifetime) =>
        PutUntil(kind, entry, timeProvider.GetUtcNow().UtcDateTime.Add(lifetime));

    private string PutUntil<T>(string kind, T entry, DateTime expiresAtUtc)
    {
        Cleanup();
        var secret = NewSecret();
        states.Put(secret, kind, JsonSerializer.Serialize(entry), expiresAtUtc);
        return secret;
    }

    private void Cleanup()
    {
#pragma warning disable ORBIT0004 // Cleanup evicts authorization codes whose CreatedAt instant precedes the CodeExpiry cutoff. https://github.com/thomasluizon/orbit-tickets/issues/230
        states.RemoveExpired(DateTime.UtcNow);
#pragma warning restore ORBIT0004
    }

    private static T? Deserialize<T>(string? payload) => payload is null ? default : JsonSerializer.Deserialize<T>(payload);
    private static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace("+", "-").Replace("/", "_").TrimEnd('=');
    private static string Challenge(string verifier) => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        .Replace("+", "-").Replace("/", "_").TrimEnd('=');
}

public record ConsentEntry(Guid UserId, string CodeChallenge, string RedirectUri, string ClientId,
    string State, string? Nonce, string Language, IReadOnlyList<string>? Scopes = null, string? ClientName = null);
public sealed record ConsentTicket(ConsentEntry Entry, bool IsConsumed);
public record RefreshEntry(Guid UserId, Guid ApiKeyId, string ClientId, Guid FamilyId = default, DateTime? FamilyExpiresAtUtc = null);

public record AuthorizationEntry(
    Guid UserId,
    string CodeChallenge,
    string RedirectUri,
    string ClientId,
    string? Nonce,
    DateTime CreatedAt,
    IReadOnlyList<string>? Scopes = null,
    bool IsReadOnly = false);

public record GoogleAuthorizationRequest(
    string GoogleState,
    string ClientId,
    string RedirectUri,
    string ClientState,
    string CodeChallenge,
    string? Nonce,
    string GoogleRedirectUri,
    string Language,
    string GoogleCodeVerifier,
    string GoogleCodeChallenge,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<string>? Scopes = null);
