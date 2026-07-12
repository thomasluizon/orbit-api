using System.Reflection;
using FluentAssertions;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;

namespace Orbit.Infrastructure.Tests.Controllers;

public class AccountabilityControllerRateLimitTests
{
    [Theory]
    [InlineData(nameof(AccountabilityController.Invite))]
    [InlineData(nameof(AccountabilityController.Accept))]
    [InlineData(nameof(AccountabilityController.End))]
    [InlineData(nameof(AccountabilityController.SetHabits))]
    [InlineData(nameof(AccountabilityController.CheckIn))]
    public void MutatingActions_AreRateLimited(string actionName)
    {
        var rateLimitAttributes = GetAction(actionName)
            .GetCustomAttributes<DistributedRateLimitAttribute>(inherit: false)
            .ToList();

        rateLimitAttributes.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(nameof(AccountabilityController.GetPairs))]
    [InlineData(nameof(AccountabilityController.GetCheckIns))]
    public void ReadActions_AreNotRateLimited(string actionName)
    {
        var rateLimitAttributes = GetAction(actionName)
            .GetCustomAttributes<DistributedRateLimitAttribute>(inherit: false)
            .ToList();

        rateLimitAttributes.Should().BeEmpty();
    }

    private static MethodInfo GetAction(string actionName)
    {
        var method = typeof(AccountabilityController).GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance);
        method.Should().NotBeNull($"AccountabilityController should expose a public action named '{actionName}'");
        return method!;
    }
}
