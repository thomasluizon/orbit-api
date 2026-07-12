using System.Reflection;
using FluentAssertions;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;

namespace Orbit.Infrastructure.Tests.Controllers;

public class AiControllerRateLimitTests
{
    [Theory]
    [InlineData(nameof(AiController.ConfirmPendingOperation))]
    [InlineData(nameof(AiController.ExecutePendingOperation))]
    public void PendingOperationActions_AreRateLimited(string actionName)
    {
        var rateLimitAttributes = GetAction(actionName)
            .GetCustomAttributes<DistributedRateLimitAttribute>(inherit: false)
            .ToList();

        rateLimitAttributes.Should().HaveCount(1);
    }

    private static MethodInfo GetAction(string actionName)
    {
        var method = typeof(AiController).GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance);
        method.Should().NotBeNull($"AiController should expose a public action named '{actionName}'");
        return method!;
    }
}
