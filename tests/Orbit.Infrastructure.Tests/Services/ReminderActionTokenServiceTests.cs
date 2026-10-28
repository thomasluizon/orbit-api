using FluentAssertions;
using Microsoft.Extensions.Options;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class ReminderActionTokenServiceTests
{
    [Fact]
    public void SignedToken_BindsEveryClaimAndRejectsTampering()
    {
        var service = new ReminderActionTokenService(Options.Create(new JwtSettings
        {
            SecretKey = new string('s', 48),
            Issuer = "orbit",
            Audience = "orbit"
        }));
        var reminderAtUtc = new DateTime(2027, 9, 26, 12, 0, 0, DateTimeKind.Utc);
        var claims = new ReminderActionClaims(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), reminderAtUtc, reminderAtUtc.AddHours(8));
        var token = service.Sign(claims);
        service.TryRead(token, out var decoded).Should().BeTrue();
        decoded.Should().Be(claims);
        service.TryRead(token[..^2] + "xx", out _).Should().BeFalse();
        service.TryRead("bad-token", out _).Should().BeFalse();
    }
}
