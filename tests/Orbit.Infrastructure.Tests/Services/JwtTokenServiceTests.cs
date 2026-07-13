using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class JwtTokenServiceTests
{
    private readonly JwtTokenService _sut;
    private readonly JwtSettings _settings;

    public JwtTokenServiceTests()
    {
        _settings = new JwtSettings
        {
            SecretKey = "test-secret-key-that-is-at-least-32-bytes-long-for-hmac",
            Issuer = "test-issuer",
            Audience = "test-audience",
            ExpiryHours = 168,
            ExpiryMinutes = 0
        };

        _sut = new JwtTokenService(Options.Create(_settings));
    }

    [Fact]
    public void GenerateToken_ReturnsNonEmptyString()
    {
        var userId = Guid.NewGuid();
        var email = "test@example.com";

        var token = _sut.GenerateToken(userId, email);

        token.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GenerateToken_ContainsUserIdClaim()
    {
        var userId = Guid.NewGuid();
        var email = "test@example.com";

        var token = _sut.GenerateToken(userId, email);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        jwt.Claims
            .Should().Contain(c =>
                c.Type == ClaimTypes.NameIdentifier && c.Value == userId.ToString());
    }

    [Fact]
    public void GenerateToken_ContainsEmailClaim()
    {
        var userId = Guid.NewGuid();
        var email = "user@orbit.test";

        var token = _sut.GenerateToken(userId, email);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        jwt.Claims
            .Should().Contain(c =>
                c.Type == ClaimTypes.Email && c.Value == email);
    }

    [Fact]
    public void GenerateToken_OmitsAdminClaim()
    {
        var token = _sut.GenerateToken(Guid.NewGuid(), "user@orbit.test");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        jwt.Claims.Should().NotContain(c =>
            c.Type.Contains("admin", StringComparison.OrdinalIgnoreCase)
            || c.Value.Contains("admin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateToken_IncludesGuidJtiClaim()
    {
        var token = _sut.GenerateToken(Guid.NewGuid(), "user@orbit.test");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        var jti = jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Jti).Value;
        Guid.TryParse(jti, out _).Should().BeTrue();
    }

    [Fact]
    public void GenerateToken_ProducesDistinctJtiPerToken()
    {
        var handler = new JwtSecurityTokenHandler();

        var firstJti = handler.ReadJwtToken(_sut.GenerateToken(Guid.NewGuid(), "a@orbit.test"))
            .Claims.Single(c => c.Type == JwtRegisteredClaimNames.Jti).Value;
        var secondJti = handler.ReadJwtToken(_sut.GenerateToken(Guid.NewGuid(), "b@orbit.test"))
            .Claims.Single(c => c.Type == JwtRegisteredClaimNames.Jti).Value;

        secondJti.Should().NotBe(firstJti);
    }

    [Fact]
    public void GenerateToken_SetsCorrectExpiry()
    {
        var userId = Guid.NewGuid();
        var email = "test@example.com";
        var beforeGeneration = DateTime.UtcNow;

        var token = _sut.GenerateToken(userId, email);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);
        var expectedExpiry = _settings.ExpiryMinutes > 0
            ? beforeGeneration.AddMinutes(_settings.ExpiryMinutes)
            : beforeGeneration.AddHours(_settings.ExpiryHours);

        jwt.ValidTo.Should().BeCloseTo(expectedExpiry, TimeSpan.FromSeconds(5));
    }
}
