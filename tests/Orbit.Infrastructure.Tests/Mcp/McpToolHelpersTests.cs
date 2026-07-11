using System.Security.Claims;
using FluentAssertions;
using Orbit.Api.Mcp.Tools;

namespace Orbit.Infrastructure.Tests.Mcp;

public class McpToolHelpersTests
{
    [Fact]
    public void GetUserId_ValidPrincipal_ReturnsUserIdFromNameIdentifierClaim()
    {
        var userId = Guid.NewGuid();
        var principal = PrincipalWithNameIdentifier(userId.ToString());

        McpToolHelpers.GetUserId(principal).Should().Be(userId);
    }

    [Fact]
    public void GetUserId_MissingNameIdentifierClaim_ThrowsUnauthorizedAccessException()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        var act = () => McpToolHelpers.GetUserId(principal);

        act.Should().Throw<UnauthorizedAccessException>()
            .WithMessage("User ID not found in token");
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("   ")]
    public void GetUserId_InvalidGuidClaim_ThrowsUnauthorizedAccessException(string claimValue)
    {
        var principal = PrincipalWithNameIdentifier(claimValue);

        var act = () => McpToolHelpers.GetUserId(principal);

        act.Should().Throw<UnauthorizedAccessException>()
            .WithMessage("User ID claim is not a valid GUID");
    }

    private static ClaimsPrincipal PrincipalWithNameIdentifier(string value)
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, value) };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }
}
