using System.Reflection;
using FluentAssertions;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;

namespace Orbit.Infrastructure.Tests.Controllers;

public sealed class EmailControllerRateLimitTests
{
    [Fact]
    public void SesEvents_UsesDistributedRateLimit()
    {
        typeof(EmailController).GetMethod(nameof(EmailController.SesEvents))!
            .GetCustomAttribute<DistributedRateLimitAttribute>()
            .Should().NotBeNull();
    }
}
