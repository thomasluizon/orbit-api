using FluentAssertions;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;

namespace Orbit.Infrastructure.Tests.RateLimiting;

public class DistributedRateLimitPartitionKeyTests
{
    [Fact]
    public void TryResolveAuthEmailPartitionKey_NormalizesEmailCasingAndWhitespace()
    {
        var upper = ResolveForAuth(new AuthController.SendCodeRequest("A@x.com"));
        var lower = ResolveForAuth(new AuthController.VerifyCodeRequest("  a@x.com ", "123456"));

        upper.Resolved.Should().BeTrue();
        lower.Resolved.Should().BeTrue();
        upper.PartitionKey.Should().Be("auth:email:a@x.com");
        lower.PartitionKey.Should().Be(upper.PartitionKey);
    }

    [Fact]
    public void TryResolveAuthEmailPartitionKey_DifferentEmailsMapToDifferentKeys()
    {
        var first = ResolveForAuth(new AuthController.SendCodeRequest("first@x.com"));
        var second = ResolveForAuth(new AuthController.SendCodeRequest("second@x.com"));

        first.PartitionKey.Should().NotBe(second.PartitionKey);
    }

    [Fact]
    public void TryResolveAuthEmailPartitionKey_FallsBackWhenNoEmailArgument()
    {
        var resolved = ResolveForAuth(new AuthController.RefreshSessionRequest("refresh-token"));

        resolved.Resolved.Should().BeFalse();
        resolved.PartitionKey.Should().BeEmpty();
    }

    [Fact]
    public void TryResolveAuthEmailPartitionKey_FallsBackWhenEmailIsBlank()
    {
        var resolved = ResolveForAuth(new AuthController.SendCodeRequest("   "));

        resolved.Resolved.Should().BeFalse();
    }

    [Fact]
    public void TryResolveAuthEmailPartitionKey_OnlyAppliesToAuthPolicy()
    {
        var resolved = DistributedRateLimitFilter.TryResolveAuthEmailPartitionKey(
            "chat",
            [new AuthController.SendCodeRequest("a@x.com")],
            out var partitionKey);

        resolved.Should().BeFalse();
        partitionKey.Should().BeEmpty();
    }

    private static (bool Resolved, string PartitionKey) ResolveForAuth(object request)
    {
        var resolved = DistributedRateLimitFilter.TryResolveAuthEmailPartitionKey(
            "auth",
            [request],
            out var partitionKey);

        return (resolved, partitionKey);
    }
}
