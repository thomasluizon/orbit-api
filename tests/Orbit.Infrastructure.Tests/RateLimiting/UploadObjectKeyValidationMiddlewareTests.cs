using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Orbit.Api.Middleware;

namespace Orbit.Infrastructure.Tests.RateLimiting;

public class UploadObjectKeyValidationMiddlewareTests
{
    [Theory]
    [InlineData("../other", "1e21a217-e146-43c9-b61c-b5871a991578.webp")]
    [InlineData("2b3b1fc7-813c-4eb6-ad02-bd7754299703", "invented.webp")]
    [InlineData("2b3b1fc7-813c-4eb6-ad02-bd7754299703", "1e21a217-e146-43c9-b61c-b5871a991578.pdf")]
    public async Task InvalidKey_Returns404BeforeLimiter(string userId, string fileName)
    {
        var limiterCalled = false;
        var middleware = new UploadObjectKeyValidationMiddleware(_ =>
        {
            limiterCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = $"/api/uploads/object/{userId}/{fileName}";
        context.Request.RouteValues["userId"] = userId;
        context.Request.RouteValues["fileName"] = fileName;

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        limiterCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ValidKey_ReachesLimiter()
    {
        var limiterCalled = false;
        var middleware = new UploadObjectKeyValidationMiddleware(_ =>
        {
            limiterCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/uploads/object/2b3b1fc7-813c-4eb6-ad02-bd7754299703/1e21a217-e146-43c9-b61c-b5871a991578.webp";
        context.Request.RouteValues["userId"] = "2b3b1fc7-813c-4eb6-ad02-bd7754299703";
        context.Request.RouteValues["fileName"] = "1e21a217-e146-43c9-b61c-b5871a991578.webp";

        await middleware.InvokeAsync(context);

        limiterCalled.Should().BeTrue();
    }
}
