using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class AppleIdentityServiceTests : IDisposable
{
    private readonly RSA _appleKey = RSA.Create(2048);
    private readonly ECDsa _clientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private const string RawNonce = "test-raw-nonce-with-entropy";
    private static string Nonce => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(RawNonce)));
    private AppleSignInSettings Settings => new()
    {
        KeyId = "KEY",
        TeamId = "TEAM",
        PrivateKey = _clientKey.ExportPkcs8PrivateKeyPem(),
        NativeClientId = "org.useorbit.app",
        WebClientId = "org.useorbit.web",
        RedirectUri = "https://api.example.com/api/auth/apple/callback",
        WebOrigin = "https://app.example.com"
    };

    private string Token(string audience = "org.useorbit.app", bool expired = false, string? nonce = null, RSA? key = null)
    {
        var content = $"{AppleJwt.Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "APPLE" }))}.{AppleJwt.Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = "https://appleid.apple.com",
            aud = audience,
            sub = "apple-subject",
            email = "relay@privaterelay.appleid.com",
            email_verified = "true",
            iat = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds(),
            exp = DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 5).ToUnixTimeSeconds(),
            nonce = nonce ?? Nonce
        }))}";
        return $"{content}.{AppleJwt.Encode((key ?? _appleKey).SignData(Encoding.ASCII.GetBytes(content), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))}";
    }

    private AppleIdentityService Service(string exchangedToken, out RecordingHandler handler)
    {
        var parameters = _appleKey.ExportParameters(false);
        handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/auth/keys" => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { keys = new[] { new { kid = "APPLE", kty = "RSA", use = "sig", alg = "RS256", n = AppleJwt.Encode(parameters.Modulus!), e = AppleJwt.Encode(parameters.Exponent!) } } })) },
            "/auth/token" => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { access_token = "access", token_type = "Bearer", expires_in = 3600, refresh_token = "refresh", id_token = exchangedToken })) },
            "/auth/revoke" => new(HttpStatusCode.OK),
            _ => throw new InvalidOperationException("Unexpected Apple endpoint")
        });
        return new(new HttpClient(handler), Options.Create(Settings), TimeProvider.System);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("nonce")]
    public async Task Native_RejectsForgedOrInvalidClaimsBeforeExchange(string failure)
    {
        using var forged = RSA.Create(2048);
        var token = Token(audience: failure == "audience" ? "attacker.app" : "org.useorbit.app", expired: failure == "expired",
            nonce: failure == "nonce" ? "different-nonce" : null, key: failure == "signature" ? forged : null);
        using var service = Service(Token(), out var handler);
        var act = () => service.AuthenticateNativeAsync(token, "code", RawNonce, default);
        await act.Should().ThrowAsync<AppleVerificationException>();
        handler.Paths.Should().NotContain("/auth/token");
    }

    [Fact]
    public async Task Native_ExchangesCodeAndCachesPublicKeys()
    {
        using var service = Service(Token(), out var handler);
        var identity = await service.AuthenticateNativeAsync(Token(), "code", RawNonce, default);
        identity.Subject.Should().Be("apple-subject");
        identity.EmailVerified.Should().BeTrue();
        identity.RefreshToken.Should().Be("refresh");
        handler.Paths.Count(path => path == "/auth/keys").Should().Be(1);
        handler.Forms.Single().Should().Contain("grant_type=authorization_code").And.Contain("client_id=org.useorbit.app");
    }

    [Fact]
    public async Task Native_ExchangedTokenHasDifferentNonce_Rejects()
    {
        using var service = Service(Token(nonce: "another-login"), out _);
        var act = () => service.AuthenticateNativeAsync(Token(), "code", RawNonce, default);
        await act.Should().ThrowAsync<AppleVerificationException>();
    }

    [Fact]
    public async Task Web_UsesServicesAudienceAndRegisteredRedirect()
    {
        using var service = Service(Token("org.useorbit.web", nonce: "web-nonce"), out var handler);
        var identity = await service.AuthenticateWebAsync("code", "web-nonce", Settings.RedirectUri, default);
        identity.ClientId.Should().Be(Settings.WebClientId);
        handler.Forms.Single().Should().Contain("redirect_uri=https%3A%2F%2Fapi.example.com%2Fapi%2Fauth%2Fapple%2Fcallback");
    }

    [Fact]
    public async Task Revoke_UsesStoredClientAndIgnoresEmptySuccessBody()
    {
        using var service = Service(Token(), out var handler);
        await service.RevokeAsync("refresh", Settings.NativeClientId, default);
        handler.Paths.Should().Equal("/auth/revoke");
        handler.Forms.Single().Should().Contain("token=refresh").And.Contain("token_type_hint=refresh_token");
    }

    public void Dispose() { _appleKey.Dispose(); _clientKey.Dispose(); }
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];
        internal List<string> Forms { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.Content is not null) Forms.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }
}
