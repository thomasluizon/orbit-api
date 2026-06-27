using System.Reflection;
using FluentAssertions;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;

namespace Orbit.Infrastructure.Tests.Controllers;

public class FriendsControllerRateLimitTests
{
    [Theory]
    [InlineData(nameof(FriendsController.SendCheer))]
    [InlineData(nameof(FriendsController.SendRequest))]
    [InlineData(nameof(FriendsController.Report))]
    [InlineData(nameof(FriendsController.Block))]
    [InlineData(nameof(FriendsController.Unblock))]
    public void AbuseProneActions_AreRateLimited(string actionName)
    {
        var rateLimitAttributes = GetAction(actionName)
            .GetCustomAttributes<DistributedRateLimitAttribute>(inherit: false)
            .ToList();

        rateLimitAttributes.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(nameof(FriendsController.AcceptRequest))]
    [InlineData(nameof(FriendsController.RemoveFriend))]
    [InlineData(nameof(FriendsController.GetFeed))]
    public void NonMutatingOrLowRiskActions_AreNotRateLimited(string actionName)
    {
        var rateLimitAttributes = GetAction(actionName)
            .GetCustomAttributes<DistributedRateLimitAttribute>(inherit: false)
            .ToList();

        rateLimitAttributes.Should().BeEmpty();
    }

    private static MethodInfo GetAction(string actionName)
    {
        var method = typeof(FriendsController).GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance);
        method.Should().NotBeNull($"FriendsController should expose a public action named '{actionName}'");
        return method!;
    }
}
