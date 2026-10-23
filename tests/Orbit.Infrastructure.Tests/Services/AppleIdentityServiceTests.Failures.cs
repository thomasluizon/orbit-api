using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class AppleIdentityServiceTests
{
    private string ChangedToken(string field, JsonNode? value, bool header = false)
    {
        var parts = Token().Split('.');
        var node = JsonNode.Parse(AppleJwt.Decode(parts[header ? 0 : 1]))!;
        node[field] = value;
        parts[header ? 0 : 1] = AppleJwt.Encode(Encoding.UTF8.GetBytes(node.ToJsonString()));
        var content = $"{parts[0]}.{parts[1]}";
        return $"{content}.{AppleJwt.Encode(_appleKey.SignData(Encoding.ASCII.GetBytes(content), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))}";
    }

    [Theory]
    [InlineData("iss", "https://attacker.example.com")]
    [InlineData("sub", "")]
    [InlineData("sub", " ")]
    [InlineData("nonce", "different")]
    public async Task Native_InvalidSignedClaims_Rejects(string claim, string value)
    {
        using var service = Service(Token(), out var handler);
        var act = () => service.AuthenticateNativeAsync(ChangedToken(claim, JsonValue.Create(value)), "code", RawNonce, default);
        await act.Should().ThrowAsync<AppleVerificationException>();
        handler.Paths.Should().NotContain("/auth/token");
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("e30.e30.invalid")]
    public async Task Native_MalformedToken_RejectsWithoutExchangingCode(string token)
    {
        using var service = Service(Token(), out var handler);
        await FluentActions.Awaiting(() => service.AuthenticateNativeAsync(token, "code", RawNonce, default))
            .Should().ThrowAsync<AppleVerificationException>();
        handler.Paths.Should().NotContain("/auth/token");
    }

    [Theory]
    [InlineData("alg", "HS256")]
    [InlineData("kid", "unknown")]
    public async Task Native_UnsupportedSigningHeader_Rejects(string field, string value)
    {
        using var service = Service(Token(), out _);
        await FluentActions.Awaiting(() => service.AuthenticateNativeAsync(ChangedToken(field, JsonValue.Create(value), header: true), "code", RawNonce, default))
            .Should().ThrowAsync<AppleVerificationException>();
    }

    [Fact]
    public async Task Native_FutureIssuedOrOversizedSubject_Rejects()
    {
        using var service = Service(Token(), out _);
        foreach (var token in new[] { ChangedToken("iat", JsonValue.Create(DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds())), ChangedToken("sub", JsonValue.Create(new string('s', 256))) })
            await FluentActions.Awaiting(() => service.AuthenticateNativeAsync(token, "code", RawNonce, default))
                .Should().ThrowAsync<AppleVerificationException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Claims_OptionalEmailAndBooleanVerification_AreRead(bool verified)
    {
        var token = ChangedToken("email_verified", JsonValue.Create(verified));
        using var service = Service(token, out _);
        var identity = await service.AuthenticateNativeAsync(token, "code", RawNonce, default);
        identity.EmailVerified.Should().Be(verified);
        var withoutEmail = ChangedToken("email", null);
        var noEmail = await service.ValidateTokenAsync(withoutEmail, Settings.NativeClientId, Nonce, default);
        noEmail.Email.Should().BeNull();
    }

    [Theory]
    [InlineData("status")]
    [InlineData("json")]
    [InlineData("empty-refresh")]
    [InlineData("oversized-refresh")]
    public async Task Native_UnusableCodeExchange_Rejects(string failure)
    {
        var token = Token();
        using var service = Service(token, out _, respond: request => request.RequestUri!.AbsolutePath == "/auth/token"
            ? new(failure == "status" ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            {
                Content = new StringContent(failure == "json" ? "invalid" : JsonSerializer.Serialize(new
                { id_token = token, refresh_token = failure == "oversized-refresh" ? new string('r', 16_385) : "" }))
            }
            : KeysResponse());
        var act = () => service.AuthenticateNativeAsync(Token(), "code", RawNonce, default);
        await act.Should().ThrowAsync<AppleVerificationException>();
    }

    [Fact]
    public async Task Native_ExchangedSubjectMismatch_Rejects()
    {
        using var service = Service(ChangedToken("sub", JsonValue.Create("another-subject")), out _);
        await FluentActions.Awaiting(() => service.AuthenticateNativeAsync(Token(), "code", RawNonce, default))
            .Should().ThrowAsync<AppleVerificationException>();
    }

    [Fact]
    public async Task ProviderEndpoints_UseConfiguredUrls()
    {
        var settings = Settings;
        settings.KeysUrl = "https://identity.example.com/auth/keys";
        settings.TokenUrl = "https://identity.example.com/auth/token";
        settings.RevokeUrl = "https://identity.example.com/auth/revoke";
        var urls = new List<string>();
        var token = Token();
        using var service = Service(token, out _, settings, request =>
        {
            urls.Add(request.RequestUri!.AbsoluteUri);
            return request.RequestUri.AbsolutePath switch
            {
                "/auth/keys" => KeysResponse(),
                "/auth/token" => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { id_token = token, refresh_token = "refresh" })) },
                _ => new(HttpStatusCode.OK)
            };
        });
        await service.AuthenticateNativeAsync(token, "code", RawNonce, default);
        await service.RevokeAsync("refresh", settings.NativeClientId, default);
        urls.Should().Equal(settings.KeysUrl, settings.TokenUrl, settings.RevokeUrl);
    }

    [Fact]
    public async Task Web_RejectsUnregisteredRedirectAndAcceptsMcpRedirect()
    {
        var settings = Settings;
        settings.McpRedirectUri = "https://api.example.com/oauth/apple/callback";
        using var service = Service(Token(settings.WebClientId, nonce: "web-nonce"), out _, settings);
        await FluentActions.Awaiting(() => service.AuthenticateWebAsync("code", "web-nonce", "https://attacker.example.com/callback", default))
            .Should().ThrowAsync<AppleVerificationException>();
        (await service.AuthenticateWebAsync("code", "web-nonce", settings.McpRedirectUri, default)).ClientId.Should().Be(settings.WebClientId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoke_InvalidClientOrProviderFailure_Rejects(bool providerFails)
    {
        using var service = Service(Token(), out var handler, respond: _ => new(HttpStatusCode.BadRequest));
        await FluentActions.Awaiting(() => service.RevokeAsync("refresh", providerFails ? Settings.NativeClientId : "unknown-client", default))
            .Should().ThrowAsync<AppleVerificationException>();
        handler.Paths.Should().HaveCount(providerFails ? 1 : 0);
    }

    [Fact]
    public async Task MissingConfiguration_DisablesAuthenticationAndRevoke()
    {
        using var service = Service(Token(), out var handler, new AppleSignInSettings());
        service.IsConfigured.Should().BeFalse();
        await FluentActions.Awaiting(() => service.AuthenticateNativeAsync(Token(), "code", RawNonce, default)).Should().ThrowAsync<AppleVerificationException>();
        await FluentActions.Awaiting(() => service.RevokeAsync("refresh", "client", default)).Should().ThrowAsync<AppleVerificationException>();
        handler.Paths.Should().BeEmpty();
    }

    private HttpResponseMessage KeysResponse()
    {
        var key = _appleKey.ExportParameters(false);
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                keys = new[]
        { new { kid = "APPLE", kty = "RSA", use = "sig", alg = "RS256", n = AppleJwt.Encode(key.Modulus!), e = AppleJwt.Encode(key.Exponent!) } }
            }))
        };
    }
}
