using FluentAssertions;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Tests.Configuration;

public class JwtSettingsTests
{
    [Fact]
    public void Validate_OmittedLifetime_DefaultsToZeroAndThrows()
    {
        var settings = new JwtSettings
        {
            SecretKey = "test-secret-key-that-is-at-least-32-bytes-long-for-hmac",
            Issuer = "test-issuer",
            Audience = "test-audience"
        };

        settings.ExpiryHours.Should().Be(0);
        var act = () => settings.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Jwt:ExpiryMinutes*Jwt:ExpiryHours*60*");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(-1, -1)]
    [InlineData(168, 0)]
    [InlineData(0, 61)]
    [InlineData(int.MaxValue, 0)]
    [InlineData(int.MinValue, 0)]
    [InlineData(0, int.MaxValue)]
    public void Validate_InvalidEffectiveLifetime_ThrowsWithSettings(int hours, int minutes)
    {
        var settings = CreateSettings(hours, minutes);
        var act = () => settings.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Jwt:ExpiryMinutes*Jwt:ExpiryHours*60*");
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 60)]
    [InlineData(168, 15)]
    [InlineData(-1, 15)]
    [InlineData(1, -1)]
    public void Validate_ValidEffectiveLifetime_DoesNotThrow(int hours, int minutes)
    {
        var settings = CreateSettings(hours, minutes);
        var act = () => settings.Validate();

        act.Should().NotThrow();
    }

    private static JwtSettings CreateSettings(int hours, int minutes) => new()
    {
        SecretKey = "test-secret-key-that-is-at-least-32-bytes-long-for-hmac",
        Issuer = "test-issuer",
        Audience = "test-audience",
        ExpiryHours = hours,
        ExpiryMinutes = minutes
    };
}
