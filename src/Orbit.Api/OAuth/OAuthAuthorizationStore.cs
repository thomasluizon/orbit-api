using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Orbit.Api.OAuth;

public sealed class OAuthAuthorizationStore : IDisposable
{
    private readonly ConcurrentDictionary<string, AuthorizationEntry> _codes = new();
    private readonly Timer _cleanupTimer;
    private static readonly TimeSpan CodeExpiry = TimeSpan.FromMinutes(5);

    public OAuthAuthorizationStore()
    {
        _cleanupTimer = new Timer(_ => Cleanup(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public string CreateCode(Guid userId, string codeChallenge, string redirectUri, string clientId)
    {
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        var entry = new AuthorizationEntry(userId, codeChallenge, redirectUri, clientId, DateTime.UtcNow);
        _codes[code] = entry;
        return code;
    }

    public AuthorizationEntry? ExchangeCode(string code, string codeVerifier, string redirectUri)
    {
        if (!_codes.TryRemove(code, out var entry))
            return null;

        if (DateTime.UtcNow - entry.CreatedAt > CodeExpiry)
            return null;

        if (entry.RedirectUri != redirectUri)
            return null;

        // Validate PKCE: SHA256(code_verifier) must match stored code_challenge
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var computed = Convert.ToBase64String(hash)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        if (computed != entry.CodeChallenge)
            return null;

        return entry;
    }

    private void Cleanup()
    {
        var cutoff = DateTime.UtcNow - CodeExpiry;
        foreach (var kvp in _codes)
        {
            if (kvp.Value.CreatedAt < cutoff)
                _codes.TryRemove(kvp.Key, out _);
        }
    }

    public void Dispose() => _cleanupTimer.Dispose();
}

public record AuthorizationEntry(
    Guid UserId,
    string CodeChallenge,
    string RedirectUri,
    string ClientId,
    DateTime CreatedAt);
