using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Orbit.Api.OAuth;

public sealed partial class OAuthAuthorizationStore : IDisposable
{
    private readonly ConcurrentDictionary<string, AuthorizationEntry> _codes = new();
    private readonly ConcurrentDictionary<string, GoogleAuthorizationRequest> _googleRequests = new();
    private readonly Timer _cleanupTimer;
    private readonly TimeProvider _timeProvider;
    private static readonly TimeSpan CodeExpiry = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Hard ceiling on pending Google sign-in requests held in memory. An entry is only allocated once a person
    /// starts Google sign-in and it lives at most <see cref="CodeExpiry"/>, so this cap bounds the store's
    /// footprint while leaving far more headroom than real concurrent sign-in traffic needs.
    /// </summary>
    internal const int MaxPendingGoogleRequests = 1_000;

    public OAuthAuthorizationStore(ILogger<OAuthAuthorizationStore> logger) : this(logger, TimeProvider.System) { }

    public OAuthAuthorizationStore(ILogger<OAuthAuthorizationStore> logger, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _cleanupTimer = new Timer(_ =>
        {
            try { Cleanup(); }
            catch (Exception ex) { LogCleanupFailed(logger, ex); }
        }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Allocates a single-use Google sign-in request, or returns <c>null</c> when the store already holds
    /// <see cref="MaxPendingGoogleRequests"/> live entries. Expired entries are evicted first, so the cap only
    /// refuses genuine pressure. The count is re-read after the insert and the new entry is withdrawn when it
    /// crossed the cap, so concurrent callers can never leave the store above its ceiling.
    /// </summary>
    public GoogleAuthorizationRequest? TryCreateGoogleRequest(string clientId, string redirectUri, string clientState,
        string codeChallenge, string? nonce, string googleRedirectUri, string language)
    {
        EvictExpiredGoogleRequests();
        if (_googleRequests.Count >= MaxPendingGoogleRequests)
            return null;

        var state = NewSecret();
        var verifier = NewSecret();
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var request = new GoogleAuthorizationRequest(state, clientId, redirectUri, clientState,
            codeChallenge, nonce, googleRedirectUri, language, verifier, challenge, _timeProvider.GetUtcNow());
        _googleRequests[state] = request;

        if (_googleRequests.Count > MaxPendingGoogleRequests)
        {
            _googleRequests.TryRemove(state, out _);
            return null;
        }

        return request;
    }

    public GoogleAuthorizationRequest? ConsumeGoogleRequest(string state) =>
        _googleRequests.TryRemove(state, out var request) && !IsExpired(request.CreatedAtUtc) ? request : null;

    private bool IsExpired(DateTimeOffset createdAtUtc) => _timeProvider.GetUtcNow() - createdAtUtc > CodeExpiry;

    private static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace("+", "-").Replace("/", "_").TrimEnd('=');

    public string CreateCode(Guid userId, string codeChallenge, string redirectUri, string clientId, string? nonce = null)
    {
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

#pragma warning disable ORBIT0004
        var entry = new AuthorizationEntry(userId, codeChallenge, redirectUri, clientId, nonce, DateTime.UtcNow);
#pragma warning restore ORBIT0004
        _codes[code] = entry;
        return code;
    }

    public AuthorizationEntry? ExchangeCode(string code, string codeVerifier, string redirectUri)
    {
        if (!_codes.TryRemove(code, out var entry))
            return null;

#pragma warning disable ORBIT0004
        if (DateTime.UtcNow - entry.CreatedAt > CodeExpiry)
#pragma warning restore ORBIT0004
            return null;

        if (entry.RedirectUri != redirectUri)
            return null;

        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var computed = Convert.ToBase64String(hash)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        if (computed != entry.CodeChallenge)
            return null;

        return entry;
    }

    private void Cleanup()
    {
#pragma warning disable ORBIT0004
        var cutoff = DateTime.UtcNow - CodeExpiry;
#pragma warning restore ORBIT0004
        foreach (var kvp in _codes)
        {
            if (kvp.Value.CreatedAt < cutoff)
                _codes.TryRemove(kvp.Key, out _);
        }
        EvictExpiredGoogleRequests();
    }

    private void EvictExpiredGoogleRequests()
    {
        foreach (var kvp in _googleRequests)
        {
            if (IsExpired(kvp.Value.CreatedAtUtc))
                _googleRequests.TryRemove(kvp.Key, out _);
        }
    }

    public void Dispose() => _cleanupTimer.Dispose();

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "OAuth authorization store cleanup failed")]
    private static partial void LogCleanupFailed(ILogger logger, Exception ex);
}

public record AuthorizationEntry(
    Guid UserId,
    string CodeChallenge,
    string RedirectUri,
    string ClientId,
    string? Nonce,
    DateTime CreatedAt);

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
    DateTimeOffset CreatedAtUtc);
