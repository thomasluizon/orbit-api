using System.Threading.RateLimiting;
using Orbit.Api.Extensions;

namespace Orbit.Api.RateLimiting;

public static class UploadReadRateLimitPolicy
{
    public const string Name = "upload-reads";
    public const int PermitLimit = 300;

    public static RateLimitPartition<string> GetPartition(HttpContext context)
        => RateLimitPartition.GetSlidingWindowLimiter(
            context.GetClientIpAddress() ?? "unknown",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = PermitLimit,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 4,
                QueueLimit = 0,
                AutoReplenishment = true
            });
}
