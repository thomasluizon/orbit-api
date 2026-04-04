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
            ExpiryMinutes = 15
        };

        _sut = new JwtTokenService(Options.Create(_settings));
    }

    [Fact]
    public void GenerateToken_ReturnsNonEmptyString()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var email = "test@example.com";

        // Act
        var token = _sut.GenerateToken(userId, email);

        // Assert
        token.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GenerateToken_ContainsUserIdClaim()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var email = "test@example.com";

        // Act
        var token = _sut.GenerateToken(userId, email);

        // Assert
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        jwt.Claims
            .Should().Contain(c =>
                c.Type == ClaimTypes.NameIdentifier && c.Value == userId.ToString());
    }

    [Fact]
    public void GenerateToken_ContainsEmailClaim()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var email = "user@orbit.test";

        // Act
        var token = _sut.GenerateToken(userId, email);

        // Assert
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        jwt.Claims
            .Should().Contain(c =>
                c.Type == ClaimTypes.Email && c.Value == email);
    }

    [Fact]
    public void GenerateToken_SetsCorrectExpiry()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var email = "test@example.com";
        var beforeGeneration = DateTime.UtcNow;

        // Act
        var token = _sut.GenerateToken(userId, email);

        // Assert
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);
        var expectedExpiry = _settings.ExpiryMinutes > 0
            ? beforeGeneration.AddMinutes(_settings.ExpiryMinutes)
            : beforeGeneration.AddHours(_settings.ExpiryHours);

        jwt.ValidTo.Should().BeCloseTo(expectedExpiry, TimeSpan.FromSeconds(5));
    }
}
