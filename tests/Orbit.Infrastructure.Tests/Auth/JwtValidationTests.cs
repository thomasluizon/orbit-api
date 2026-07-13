using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Tests.Auth;

public class JwtValidationTests
{
    private readonly JwtSettings _settings = new()
    {
        SecretKey = "test-secret-key-that-is-at-least-32-bytes-long-for-hmac",
        Issuer = "https://api.orbit.test",
        Audience = "orbit-clients",
        ExpiryHours = 168,
        ExpiryMinutes = 0
    };

    private TokenValidationParameters BuildAppParameters() => new()
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = _settings.Issuer,
        ValidAudience = _settings.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SecretKey)),
        ClockSkew = TimeSpan.Zero
    };

    private static string CreateToken(
        string secretKey,
        string issuer,
        string audience,
        DateTime expires,
        DateTime? notBefore = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Email, "user@orbit.test")
            ]),
            NotBefore = notBefore ?? expires.AddHours(-1),
            Expires = expires,
            Issuer = issuer,
            Audience = audience,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    [Fact]
    public async Task Validate_WellFormedToken_Succeeds()
    {
        var token = CreateToken(
            _settings.SecretKey, _settings.Issuer, _settings.Audience,
            expires: DateTime.UtcNow.AddMinutes(30));

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, BuildAppParameters());

        result.IsValid.Should().BeTrue();
        result.Exception.Should().BeNull();
    }

    [Fact]
    public async Task Validate_ExpiredToken_Fails()
    {
        var token = CreateToken(
            _settings.SecretKey, _settings.Issuer, _settings.Audience,
            expires: DateTime.UtcNow.AddMinutes(-5),
            notBefore: DateTime.UtcNow.AddMinutes(-30));

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, BuildAppParameters());

        result.IsValid.Should().BeFalse();
        result.Exception.Should().BeOfType<SecurityTokenExpiredException>();
    }

    [Fact]
    public async Task Validate_TamperedSignature_Fails()
    {
        var token = CreateToken(
            _settings.SecretKey, _settings.Issuer, _settings.Audience,
            expires: DateTime.UtcNow.AddMinutes(30));
        var parts = token.Split('.');
        var flipped = parts[2][0] == 'A' ? 'B' : 'A';
        var tampered = $"{parts[0]}.{parts[1]}.{flipped}{parts[2][1..]}";

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(tampered, BuildAppParameters());

        result.IsValid.Should().BeFalse();
        result.Exception.Should().BeOfType<SecurityTokenSignatureKeyNotFoundException>();
    }

    [Fact]
    public async Task Validate_IssuerMismatch_Fails()
    {
        var token = CreateToken(
            _settings.SecretKey, issuer: "https://evil.example.com", audience: _settings.Audience,
            expires: DateTime.UtcNow.AddMinutes(30));

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, BuildAppParameters());

        result.IsValid.Should().BeFalse();
        result.Exception.Should().BeOfType<SecurityTokenInvalidIssuerException>();
    }

    [Fact]
    public async Task Validate_AudienceMismatch_Fails()
    {
        var token = CreateToken(
            _settings.SecretKey, issuer: _settings.Issuer, audience: "some-other-audience",
            expires: DateTime.UtcNow.AddMinutes(30));

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, BuildAppParameters());

        result.IsValid.Should().BeFalse();
        result.Exception.Should().BeOfType<SecurityTokenInvalidAudienceException>();
    }
}
