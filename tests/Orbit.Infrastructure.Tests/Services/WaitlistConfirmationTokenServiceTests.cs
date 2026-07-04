using FluentAssertions;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class WaitlistConfirmationTokenServiceTests
{
    private readonly MutableTimeProvider _clock = new(DateTimeOffset.Parse("2026-07-03T12:00:00Z"));

    private WaitlistConfirmationTokenService BuildService(string signingKey = "super-secret-signing-key", int lifetimeHours = 48)
        => new(
            Options.Create(new WaitlistSettings { SigningKey = signingKey, TokenLifetimeHours = lifetimeHours }),
            _clock);

    [Fact]
    public void CreateAndValidate_RoundTripsEmailAndLanguage()
    {
        var service = BuildService();

        var token = service.CreateToken("user@test.com", "pt-BR");
        var valid = service.TryValidateToken(token, out var email, out var language);

        valid.Should().BeTrue();
        email.Should().Be("user@test.com");
        language.Should().Be("pt-BR");
    }

    [Fact]
    public void Validate_TamperedPayload_IsRejected()
    {
        var service = BuildService();
        var token = service.CreateToken("user@test.com", "en");

        var tampered = "x" + token[1..];

        service.TryValidateToken(tampered, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Validate_WrongSigningKey_IsRejected()
    {
        var token = BuildService(signingKey: "key-a").CreateToken("user@test.com", "en");

        var otherService = BuildService(signingKey: "key-b");

        otherService.TryValidateToken(token, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Validate_ExpiredToken_IsRejected()
    {
        var service = BuildService(lifetimeHours: 48);
        var token = service.CreateToken("user@test.com", "en");

        _clock.Advance(TimeSpan.FromHours(49));

        service.TryValidateToken(token, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Validate_JustBeforeExpiry_IsAccepted()
    {
        var service = BuildService(lifetimeHours: 48);
        var token = service.CreateToken("user@test.com", "en");

        _clock.Advance(TimeSpan.FromHours(47));

        service.TryValidateToken(token, out _, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-separator")]
    [InlineData("only.")]
    [InlineData(".only")]
    [InlineData("!!!.@@@")]
    public void Validate_MalformedToken_IsRejected(string token)
    {
        BuildService().TryValidateToken(token, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void CreateToken_MissingSigningKey_ThrowsLoudly()
    {
        var service = BuildService(signingKey: "");

        var act = () => service.CreateToken("user@test.com", "en");

        act.Should().Throw<InvalidOperationException>().WithMessage("*SigningKey*");
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
