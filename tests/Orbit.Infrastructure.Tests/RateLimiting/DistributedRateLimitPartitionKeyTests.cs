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

    private const string RefreshTokenA =
        "AAAA1111BBBB2222CCCC3333DDDD4444EEEE5555FFFF6666AAAA1111BBBB2222CCCC3333DDDD4444EEEE5555FFFF6666AAAA1111BBBB2222CCCC3333DDDD4444EEEE5555";

    private const string RefreshTokenB =
        "1111AAAA2222BBBB3333CCCC4444DDDD5555EEEE6666FFFF1111AAAA2222BBBB3333CCCC4444DDDD5555EEEE6666FFFF1111AAAA2222BBBB3333CCCC4444DDDD5555EEEE";

    [Fact]
    public void TryResolveRefreshTokenPartitionKey_Refresh_HashesTokenUnderTokenPrefixWithoutLeakingSecret()
    {
        var resolved = ResolveRefreshFor("refresh", new AuthController.RefreshSessionRequest(RefreshTokenA));

        resolved.Resolved.Should().BeTrue();
        resolved.PartitionKey.Should().StartWith("refresh:token:");
        resolved.PartitionKey.Should().NotContain(RefreshTokenA);
    }

    [Fact]
    public void TryResolveRefreshTokenPartitionKey_Refresh_SameTokenMapsToSameKeyAcrossRequests()
    {
        var first = ResolveRefreshFor("refresh", new AuthController.RefreshSessionRequest(RefreshTokenA));
        var second = ResolveRefreshFor("refresh", new AuthController.RefreshSessionOperationRequest(RefreshTokenA));

        first.PartitionKey.Should().Be(second.PartitionKey);
    }

    [Fact]
    public void TryResolveRefreshTokenPartitionKey_Refresh_DifferentTokensMapToDifferentKeys()
    {
        var first = ResolveRefreshFor("refresh", new AuthController.RefreshSessionRequest(RefreshTokenA));
        var second = ResolveRefreshFor("refresh", new AuthController.RefreshSessionRequest(RefreshTokenB));

        first.PartitionKey.Should().NotBe(second.PartitionKey);
    }

    [Fact]
    public void TryResolveRefreshTokenPartitionKey_Refresh_FallsBackWhenTokenIsBlank()
    {
        var resolved = ResolveRefreshFor("refresh", new AuthController.RefreshSessionRequest("   "));

        resolved.Resolved.Should().BeFalse();
        resolved.PartitionKey.Should().BeEmpty();
    }

    [Fact]
    public void TryResolveRefreshTokenPartitionKey_Refresh_FallsBackWhenNoTokenArgument()
    {
        var resolved = ResolveRefreshFor("refresh", new AuthController.SendCodeRequest("a@x.com"));

        resolved.Resolved.Should().BeFalse();
    }

    [Fact]
    public void TryResolveRefreshTokenPartitionKey_DoesNotApplyToUnrelatedPolicy()
    {
        var resolved = ResolveRefreshFor("auth", new AuthController.RefreshSessionRequest(RefreshTokenA));

        resolved.Resolved.Should().BeFalse();
        resolved.PartitionKey.Should().BeEmpty();
    }

    private static (bool Resolved, string PartitionKey) ResolveFor(string policyName, object request)
    {
        var resolved = DistributedRateLimitFilter.TryResolveEmailPartitionKey(
            policyName,
            [request],
            out var partitionKey);

        return (resolved, partitionKey);
    }

    private static (bool Resolved, string PartitionKey) ResolveRefreshFor(string policyName, object request)
    {
        var resolved = DistributedRateLimitFilter.TryResolveRefreshTokenPartitionKey(
            policyName,
            [request],
            out var partitionKey);

        return (resolved, partitionKey);
    }
}
