using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Orbit.Api.Extensions;

namespace Orbit.Infrastructure.Tests.Extensions;

public class HttpContextExtensionsTests
{
    [Fact]
    public void GetClientIpAddress_ResolvesFromRemoteIpAddress()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7");

        httpContext.GetClientIpAddress().Should().Be("198.51.100.7");
    }

    [Theory]
    [InlineData("X-Forwarded-For", "203.0.113.10, 10.0.0.1")]
    [InlineData("CF-Connecting-IP", "203.0.113.10")]
    [InlineData("X-Real-IP", "203.0.113.10")]
    public void GetClientIpAddress_IgnoresSpoofedForwardingHeaders(string headerName, string spoofedValue)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
        httpContext.Request.Headers[headerName] = spoofedValue;

        httpContext.GetClientIpAddress().Should().Be("127.0.0.1");
    }

    [Fact]
    public void GetClientIpAddress_NoRemoteIp_ReturnsNull()
    {
        var httpContext = new DefaultHttpContext();

        httpContext.GetClientIpAddress().Should().BeNull();
    }

    [Fact]
    public void GetUserId_ValidClaim_ReturnsGuid()
    {
        var userId = Guid.NewGuid();
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) };
        var identity = new ClaimsIdentity(claims, "test");
        var principal = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = principal };

        var result = httpContext.GetUserId();

        result.Should().Be(userId);
    }

    [Fact]
    public void GetUserId_NoClaim_ThrowsUnauthorizedAccessException()
    {
        var httpContext = new DefaultHttpContext();

        var act = () => httpContext.GetUserId();

        act.Should().Throw<UnauthorizedAccessException>();
    }
}
