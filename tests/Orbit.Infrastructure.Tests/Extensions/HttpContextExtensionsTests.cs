using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Orbit.Api.Extensions;

namespace Orbit.Infrastructure.Tests.Extensions;

public class HttpContextExtensionsTests
{
    [Fact]
    public void GetUserId_ValidClaim_ReturnsGuid()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) };
        var identity = new ClaimsIdentity(claims, "test");
        var principal = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = principal };

        // Act
        var result = httpContext.GetUserId();

        // Assert
        result.Should().Be(userId);
    }

    [Fact]
    public void GetUserId_NoClaim_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();

        // Act
        var act = () => httpContext.GetUserId();

        // Assert
        act.Should().Throw<UnauthorizedAccessException>();
    }
}
