using FluentAssertions;
using Microsoft.Extensions.Options;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class HabitRankingTokenServiceTests
{
    [Fact]
    public void SignedPayload_RoundTrips_AndRejectsTamperingAndDifferentKeys()
    {
        var service = Create("a-signing-key-used-only-by-this-test");
        const string payload = "{\"purpose\":\"ranking-query\"}";
        var token = service.Sign(payload);
        service.TryRead(token, out var read).Should().BeTrue();
        read.Should().Be(payload);
        service.TryRead("x" + token[1..], out _).Should().BeFalse();
        Create("another-signing-key-used-only-by-this-test").TryRead(token, out _).Should().BeFalse();
        service.TryRead("bad", out _).Should().BeFalse();
        service.TryRead(new string('a', 16385), out _).Should().BeFalse();
    }

    private static HabitRankingTokenService Create(string key) => new(Options.Create(new JwtSettings
    {
        SecretKey = key,
        Issuer = "test",
        Audience = "test"
    }));
}
