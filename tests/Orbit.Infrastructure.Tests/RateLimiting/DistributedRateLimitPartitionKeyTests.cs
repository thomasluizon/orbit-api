using FluentAssertions;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;

namespace Orbit.Infrastructure.Tests.RateLimiting;

public class DistributedRateLimitPartitionKeyTests
{
    [Fact]
    public void TryResolveEmailPartitionKey_Auth_NormalizesEmailCasingAndWhitespace()
    {
        var upper = ResolveFor("auth", new AuthController.SendCodeRequest("A@x.com"));
        var lower = ResolveFor("auth", new AuthController.VerifyCodeRequest("  a@x.com ", "123456"));

        upper.Resolved.Should().BeTrue();
        lower.Resolved.Should().BeTrue();
        upper.PartitionKey.Should().Be("auth:email:a@x.com");
        lower.PartitionKey.Should().Be(upper.PartitionKey);
    }

    [Fact]
    public void TryResolveEmailPartitionKey_Auth_DifferentEmailsMapToDifferentKeys()
    {
        var first = ResolveFor("auth", new AuthController.SendCodeRequest("first@x.com"));
        var second = ResolveFor("auth", new AuthController.SendCodeRequest("second@x.com"));

        first.PartitionKey.Should().NotBe(second.PartitionKey);
    }

    [Fact]
    public void TryResolveEmailPartitionKey_Auth_FallsBackWhenNoEmailArgument()
    {
        var resolved = ResolveFor("auth", new AuthController.RefreshSessionRequest("refresh-token"));

        resolved.Resolved.Should().BeFalse();
        resolved.PartitionKey.Should().BeEmpty();
    }

    [Fact]
    public void TryResolveEmailPartitionKey_Auth_FallsBackWhenEmailIsBlank()
    {
        var resolved = ResolveFor("auth", new AuthController.SendCodeRequest("   "));

        resolved.Resolved.Should().BeFalse();
    }

    [Fact]
    public void TryResolveEmailPartitionKey_Waitlist_PartitionsByEmailUnderOwnPrefix()
    {
        var resolved = ResolveFor("waitlist", new WaitlistController.JoinWaitlistRequest(" A@x.com "));

        resolved.Resolved.Should().BeTrue();
        resolved.PartitionKey.Should().Be("waitlist:email:a@x.com");
    }

    [Fact]
    public void TryResolveEmailPartitionKey_Waitlist_SharesNoBucketWithAuth()
    {
        var auth = ResolveFor("auth", new AuthController.SendCodeRequest("a@x.com"));
        var waitlist = ResolveFor("waitlist", new WaitlistController.JoinWaitlistRequest("a@x.com"));

        waitlist.PartitionKey.Should().NotBe(auth.PartitionKey);
    }

    [Fact]
    public void TryResolveEmailPartitionKey_DoesNotApplyToUnrelatedPolicy()
    {
        var resolved = DistributedRateLimitFilter.TryResolveEmailPartitionKey(
            "chat",
            [new AuthController.SendCodeRequest("a@x.com")],
            out var partitionKey);

        resolved.Should().BeFalse();
        partitionKey.Should().BeEmpty();
    }

    private static (bool Resolved, string PartitionKey) ResolveFor(string policyName, object request)
    {
        var resolved = DistributedRateLimitFilter.TryResolveEmailPartitionKey(
            policyName,
            [request],
            out var partitionKey);

        return (resolved, partitionKey);
    }
}
