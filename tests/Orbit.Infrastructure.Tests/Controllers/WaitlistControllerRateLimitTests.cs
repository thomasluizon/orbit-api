using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;

namespace Orbit.Infrastructure.Tests.Controllers;

public class WaitlistControllerRateLimitTests
{
    [Fact]
    public void Join_IsRateLimitedUnderWaitlistPolicy()
    {
        var attribute = GetAction(nameof(WaitlistController.Join))
            .GetCustomAttributes<DistributedRateLimitAttribute>(inherit: false)
            .ToList();

        attribute.Should().HaveCount(1);
    }

    [Fact]
    public void Join_UsesLeastPrivilegeLandingCorsPolicy()
    {
        var cors = GetAction(nameof(WaitlistController.Join))
            .GetCustomAttribute<EnableCorsAttribute>(inherit: false);

        cors.Should().NotBeNull();
        cors!.PolicyName.Should().Be("Landing");
    }

    [Fact]
    public void Controller_AllowsAnonymous()
    {
        typeof(WaitlistController)
            .GetCustomAttribute<AllowAnonymousAttribute>(inherit: false)
            .Should().NotBeNull();
    }

    [Fact]
    public void Confirm_IsNotRateLimited()
    {
        var attribute = GetAction(nameof(WaitlistController.Confirm))
            .GetCustomAttributes<DistributedRateLimitAttribute>(inherit: false)
            .ToList();

        attribute.Should().BeEmpty();
    }

    private static MethodInfo GetAction(string actionName)
    {
        var method = typeof(WaitlistController).GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance);
        method.Should().NotBeNull($"WaitlistController should expose a public action named '{actionName}'");
        return method!;
    }
}
