using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Orbit.Api.Idempotency;

namespace Orbit.Infrastructure.Tests.Idempotency;

public class HttpIdempotencyContextTests
{
    private const string HeaderName = "Idempotency-Key";

    [Fact]
    public void TryGetRequestKey_WithHeaderAndAuthenticatedUser_ReturnsKeyAndUserId()
    {
        var userId = Guid.NewGuid();
        var sut = CreateSut(BuildContext("mutation-key-1", userId.ToString()));

        var result = sut.TryGetRequestKey(out var resolvedUserId, out var idempotencyKey);

        result.Should().BeTrue();
        resolvedUserId.Should().Be(userId);
        idempotencyKey.Should().Be("mutation-key-1");
    }

    [Fact]
    public void TryGetRequestKey_WhitespacePaddedHeader_IsTrimmed()
    {
        var userId = Guid.NewGuid();
        var sut = CreateSut(BuildContext("  mutation-key-1  ", userId.ToString()));

        var result = sut.TryGetRequestKey(out _, out var idempotencyKey);

        result.Should().BeTrue();
        idempotencyKey.Should().Be("mutation-key-1");
    }

    [Fact]
    public void TryGetRequestKey_WithNoHttpContext_ReturnsFalse()
    {
        var sut = CreateSut(null);

        sut.TryGetRequestKey(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetRequestKey_WithoutHeader_ReturnsFalse()
    {
        var sut = CreateSut(BuildContext(null, Guid.NewGuid().ToString()));

        sut.TryGetRequestKey(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetRequestKey_WithOversizedHeader_ReturnsFalse()
    {
        var sut = CreateSut(BuildContext(new string('a', 201), Guid.NewGuid().ToString()));

        sut.TryGetRequestKey(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetRequestKey_WithHeaderButNoUserClaim_ReturnsFalse()
    {
        var sut = CreateSut(BuildContext("mutation-key-1", userIdClaim: null));

        sut.TryGetRequestKey(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetRequestKey_WithNonGuidUserClaim_ReturnsFalse()
    {
        var sut = CreateSut(BuildContext("mutation-key-1", "not-a-guid"));

        sut.TryGetRequestKey(out _, out _).Should().BeFalse();
    }

    private static HttpIdempotencyContext CreateSut(HttpContext? httpContext)
    {
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        httpContextAccessor.HttpContext.Returns(httpContext);
        return new HttpIdempotencyContext(httpContextAccessor);
    }

    private static DefaultHttpContext BuildContext(string? headerValue, string? userIdClaim)
    {
        var context = new DefaultHttpContext();

        if (headerValue is not null)
            context.Request.Headers[HeaderName] = headerValue;

        var claims = userIdClaim is null
            ? Array.Empty<Claim>()
            : [new Claim(ClaimTypes.NameIdentifier, userIdClaim)];
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        return context;
    }
}
