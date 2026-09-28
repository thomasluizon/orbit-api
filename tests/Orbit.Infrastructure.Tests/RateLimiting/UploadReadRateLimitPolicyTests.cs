using System.Net;
using System.Threading.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Orbit.Api.RateLimiting;

namespace Orbit.Infrastructure.Tests.RateLimiting;

public class UploadReadRateLimitPolicyTests
{
    [Fact]
    public void DistinctImagesFromOneIp_FitWithinBudget_ThenSecondIpIsIndependent()
    {
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(UploadReadRateLimitPolicy.GetPartition);
        const string userId = "2b3b1fc7-813c-4eb6-ad02-bd7754299703";

        for (var index = 0; index < 200; index++)
        {
            var context = CreateContext("203.0.113.7", userId, $"{Guid.NewGuid():D}.webp");
            using var lease = limiter.AttemptAcquire(context);
            lease.IsAcquired.Should().BeTrue();
        }

        for (var index = 200; index < UploadReadRateLimitPolicy.PermitLimit; index++)
        {
            var context = CreateContext("203.0.113.7", userId, $"{Guid.NewGuid():D}.webp");
            using var lease = limiter.AttemptAcquire(context);
            lease.IsAcquired.Should().BeTrue();
        }

        using var rejected = limiter.AttemptAcquire(CreateContext("203.0.113.7", userId, "1e21a217-e146-43c9-b61c-b5871a991578.webp"));
        rejected.IsAcquired.Should().BeFalse();

        using var otherIp = limiter.AttemptAcquire(CreateContext("203.0.113.8", userId, "1e21a217-e146-43c9-b61c-b5871a991578.webp"));
        otherIp.IsAcquired.Should().BeTrue();
    }

    private static DefaultHttpContext CreateContext(string ip, string userId, string fileName)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        context.Request.RouteValues["userId"] = userId;
        context.Request.RouteValues["fileName"] = fileName;
        return context;
    }
}
