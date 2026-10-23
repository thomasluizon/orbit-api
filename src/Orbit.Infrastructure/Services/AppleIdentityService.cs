using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;

namespace Orbit.Infrastructure.Services;

public sealed class AppleIdentityService(HttpClient http, IOptions<AppleSignInSettings> settings, TimeProvider time) : IAppleIdentityService, IDisposable
{
    private readonly AppleSignInSettings _settings = settings.Value;
    private readonly SemaphoreSlim _keyLock = new(1);
    private Dictionary<string, RSAParameters> _keys = new(StringComparer.Ordinal);
    private DateTimeOffset _keysFetchedAtUtc;
    public bool IsConfigured => _settings.IsConfigured;

    public async Task<AppleIdentity> AuthenticateNativeAsync(string identityToken, string authorizationCode, string rawNonce, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var nonce = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawNonce)));
        var identity = await ValidateTokenAsync(identityToken, _settings.NativeClientId, nonce, cancellationToken);
        var exchanged = await ExchangeAsync(authorizationCode, _settings.NativeClientId, null, nonce, cancellationToken);
        if (exchanged.Subject != identity.Subject)
            throw new AppleVerificationException();
        return exchanged;
    }

    public Task<AppleIdentity> AuthenticateWebAsync(string authorizationCode, string nonce, string redirectUri, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        if (redirectUri != _settings.RedirectUri && (!_settings.IsMcpConfigured || redirectUri != _settings.McpRedirectUri))
            throw new AppleVerificationException();
        return ExchangeAsync(authorizationCode, _settings.WebClientId, redirectUri, nonce, cancellationToken);
    }

    private async Task<AppleIdentity> ExchangeAsync(string code, string clientId, string? redirectUri, string nonce, CancellationToken cancellationToken)
    {
        try
        {
            var fields = new Dictionary<string, string> { ["client_id"] = clientId, ["client_secret"] = ClientSecret(clientId), ["code"] = code, ["grant_type"] = "authorization_code" };
            if (redirectUri is not null)
                fields["redirect_uri"] = redirectUri;
            using var response = await http.PostAsync("https://appleid.apple.com/auth/token", new FormUrlEncodedContent(fields), cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new AppleVerificationException();
            using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var identity = await ValidateTokenAsync(payload.RootElement.GetProperty("id_token").GetString()!, clientId, nonce, cancellationToken);
            var refreshToken = payload.RootElement.GetProperty("refresh_token").GetString();
            if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 16_384)
                throw new AppleVerificationException();
            return identity with { RefreshToken = refreshToken };
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException
            or JsonException or CryptographicException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new AppleVerificationException();
        }
    }

    internal async Task<AppleIdentity> ValidateTokenAsync(string token, string audience, string expectedNonce, CancellationToken cancellationToken)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3 || token.Length > 16_384)
                throw new AppleVerificationException();
            using var header = JsonDocument.Parse(AppleJwt.Decode(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "RS256")
                throw new AppleVerificationException();
            var keyId = header.RootElement.GetProperty("kid").GetString()!;
            var key = await KeyAsync(keyId, cancellationToken);
            using var rsa = RSA.Create();
            rsa.ImportParameters(key);
            if (!rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), AppleJwt.Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new AppleVerificationException();
            using var payload = JsonDocument.Parse(AppleJwt.Decode(parts[1]));
            var json = payload.RootElement;
            if (json.GetProperty("iss").GetString() != "https://appleid.apple.com" || json.GetProperty("aud").GetString() != audience
                || json.GetProperty("exp").GetInt64() <= time.GetUtcNow().ToUnixTimeSeconds()
                || json.GetProperty("iat").GetInt64() > time.GetUtcNow().AddMinutes(1).ToUnixTimeSeconds()
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(json.GetProperty("nonce").GetString()!), Encoding.UTF8.GetBytes(expectedNonce)))
                throw new AppleVerificationException();
            var subject = json.GetProperty("sub").GetString();
            if (string.IsNullOrWhiteSpace(subject) || subject.Length > 255)
                throw new AppleVerificationException();
            var email = json.TryGetProperty("email", out var emailClaim) && emailClaim.ValueKind == JsonValueKind.String ? emailClaim.GetString() : null;
            var verified = json.TryGetProperty("email_verified", out var verifiedClaim)
                && (verifiedClaim.ValueKind == JsonValueKind.True || verifiedClaim.ValueKind == JsonValueKind.String && verifiedClaim.GetString() == "true");
            return new(subject, email, verified, "", audience);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException
            or JsonException or CryptographicException or FormatException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new AppleVerificationException();
        }
    }

    private async Task<RSAParameters> KeyAsync(string keyId, CancellationToken cancellationToken)
    {
        await _keyLock.WaitAsync(cancellationToken);
        try
        {
            var age = time.GetUtcNow() - _keysFetchedAtUtc;
            if (age >= TimeSpan.FromHours(6) || !_keys.ContainsKey(keyId) && age >= TimeSpan.FromMinutes(1))
            {
                using var response = await http.GetAsync("https://appleid.apple.com/auth/keys", cancellationToken);
                response.EnsureSuccessStatusCode();
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var keys = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
                foreach (var item in json.RootElement.GetProperty("keys").EnumerateArray())
                {
                    if (item.GetProperty("kty").GetString() != "RSA" || item.GetProperty("alg").GetString() != "RS256" || item.GetProperty("use").GetString() != "sig")
                        continue;
                    keys[item.GetProperty("kid").GetString()!] = new RSAParameters { Modulus = AppleJwt.Decode(item.GetProperty("n").GetString()!), Exponent = AppleJwt.Decode(item.GetProperty("e").GetString()!) };
                }
                _keys = keys;
                _keysFetchedAtUtc = time.GetUtcNow();
            }
            return _keys.TryGetValue(keyId, out var key) ? key : throw new AppleVerificationException();
        }
        finally { _keyLock.Release(); }
    }

    public async Task RevokeAsync(string refreshToken, string clientId, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        if (clientId != _settings.NativeClientId && clientId != _settings.WebClientId)
            throw new AppleVerificationException();
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = ClientSecret(clientId),
            ["token"] = refreshToken,
            ["token_type_hint"] = "refresh_token"
        });
        using var response = await http.PostAsync("https://appleid.apple.com/auth/revoke", body, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new AppleVerificationException();
    }

    private string ClientSecret(string clientId)
    {
        var now = time.GetUtcNow();
        return AppleJwt.Sign(_settings.KeyId, _settings.PrivateKey, new
        { iss = _settings.TeamId, iat = now.ToUnixTimeSeconds(), exp = now.AddMinutes(15).ToUnixTimeSeconds(), aud = "https://appleid.apple.com", sub = clientId });
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new AppleVerificationException();
    }

    public void Dispose() { _keyLock.Dispose(); http.Dispose(); }
}
