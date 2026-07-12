using System.Reflection;
using FluentAssertions;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;

namespace Orbit.Infrastructure.Tests.Controllers;

public class AchievementsControllerRateLimitTests
{
    [Fact]
    public void ReportEvent_IsRateLimited()
    {
        var rateLimitAttributes = GetAction(nameof(AchievementsController.ReportEvent))
            .GetCustomAttributes<DistributedRateLimitAttribute>(inherit: false)
            .ToList();

        rateLimitAttributes.Should().HaveCount(1);
    }

    private static MethodInfo GetAction(string actionName)
    {
        var method = typeof(AchievementsController).GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance);
        method.Should().NotBeNull($"AchievementsController should expose a public action named '{actionName}'");
        return method!;
    }
}
