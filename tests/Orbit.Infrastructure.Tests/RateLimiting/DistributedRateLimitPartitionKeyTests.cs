using FluentAssertions;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;
using Orbit.Application.Auth.Validators;

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

    private static readonly string RefreshTokenA = new('A', RefreshTokenRules.TokenLength);

    private static readonly string RefreshTokenB = new('B', RefreshTokenRules.TokenLength);

    [Fact]
    public void BuildRefreshTokenPartitionKey_HashesTokenUnderTokenPrefixWithoutLeakingSecret()
    {
        var partitionKey = DistributedRateLimitFilter.BuildRefreshTokenPartitionKey("refresh", RefreshTokenA);

        partitionKey.Should().StartWith("refresh:token:");
        partitionKey.Should().NotContain(RefreshTokenA);
    }

    [Fact]
    public void BuildRefreshTokenPartitionKey_SameTokenMapsToSameKey()
    {
        var first = DistributedRateLimitFilter.BuildRefreshTokenPartitionKey("refresh", RefreshTokenA);
        var second = DistributedRateLimitFilter.BuildRefreshTokenPartitionKey("refresh", RefreshTokenA);

        first.Should().Be(second);
    }

    [Fact]
    public void BuildRefreshTokenPartitionKey_DifferentTokensMapToDifferentKeys()
    {
        var first = DistributedRateLimitFilter.BuildRefreshTokenPartitionKey("refresh", RefreshTokenA);
        var second = DistributedRateLimitFilter.BuildRefreshTokenPartitionKey("refresh", RefreshTokenB);

        first.Should().NotBe(second);
    }

    [Fact]
    public void TryExtractRefreshToken_Refresh_ReturnsWellFormedTokenAcrossRequestShapes()
    {
        var fromPlain = ExtractRefreshFor("refresh", new AuthController.RefreshSessionRequest(RefreshTokenA));
        var fromOperation = ExtractRefreshFor("refresh", new AuthController.RefreshSessionOperationRequest(RefreshTokenA));

        fromPlain.Resolved.Should().BeTrue();
        fromPlain.RefreshToken.Should().Be(RefreshTokenA);
        fromOperation.Resolved.Should().BeTrue();
        fromOperation.RefreshToken.Should().Be(RefreshTokenA);
    }

    [Fact]
    public void TryExtractRefreshToken_Refresh_FallsBackWhenTokenIsBlank()
    {
        var resolved = ExtractRefreshFor("refresh", new AuthController.RefreshSessionRequest("   "));

        resolved.Resolved.Should().BeFalse();
        resolved.RefreshToken.Should().BeEmpty();
    }

    [Theory]
    [InlineData("short")]
    [InlineData("g-not-hex-but-right-length-padding-000000000000000000000000000000000000000000000000000000000000000000000000000000000000000")]
    public void TryExtractRefreshToken_Refresh_FallsBackWhenTokenIsMalformed(string malformedToken)
    {
        var lowercase = new string('a', RefreshTokenRules.TokenLength);

        ExtractRefreshFor("refresh", new AuthController.RefreshSessionRequest(malformedToken)).Resolved.Should().BeFalse();
        ExtractRefreshFor("refresh", new AuthController.RefreshSessionRequest(lowercase)).Resolved.Should().BeFalse();
    }

    [Fact]
    public void TryExtractRefreshToken_Refresh_FallsBackWhenNoTokenArgument()
    {
        var resolved = ExtractRefreshFor("refresh", new AuthController.SendCodeRequest("a@x.com"));

        resolved.Resolved.Should().BeFalse();
    }

    [Fact]
    public void TryExtractRefreshToken_DoesNotApplyToUnrelatedPolicy()
    {
        var resolved = ExtractRefreshFor("auth", new AuthController.RefreshSessionRequest(RefreshTokenA));

        resolved.Resolved.Should().BeFalse();
        resolved.RefreshToken.Should().BeEmpty();
    }

    private static (bool Resolved, string PartitionKey) ResolveFor(string policyName, object request)
    {
        var resolved = DistributedRateLimitFilter.TryResolveEmailPartitionKey(
            policyName,
            [request],
            out var partitionKey);

        return (resolved, partitionKey);
    }

    private static (bool Resolved, string RefreshToken) ExtractRefreshFor(string policyName, object request)
    {
        var resolved = DistributedRateLimitFilter.TryExtractRefreshToken(
            policyName,
            [request],
            out var refreshToken);

        return (resolved, refreshToken);
    }
}
