using Microsoft.Extensions.Logging;
using Orbit.Domain.Interfaces;
using PostHog;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Sends product-analytics events to the PostHog US project. <see cref="IPostHogClient.Capture"/> only
/// enqueues onto the SDK's batching queue, so the call returns without waiting on the network.
/// </summary>
public sealed partial class PostHogProductAnalytics(
    IPostHogClient postHogClient,
    ILogger<PostHogProductAnalytics> logger) : IProductAnalytics
{
    public void CaptureUserEvent(Guid userId, string eventName, string plan)
    {
        var distinctId = userId.ToString();

        var enqueued = postHogClient.Capture(distinctId, eventName, new Dictionary<string, object>
        {
            ["$set"] = new Dictionary<string, object>
            {
                ["plan"] = plan
            },
            ["$unset"] = new[] { "isYearlyPro" }
        });

        LogCaptureEnqueued(logger, eventName, distinctId, enqueued);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "PostHog capture of {EventName} for {DistinctId} enqueued: {Enqueued}")]
    private static partial void LogCaptureEnqueued(ILogger logger, string eventName, string distinctId, bool enqueued);
}
