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
        CaptureUserEvent(userId, eventName, plan, new Dictionary<string, object>());
    }

    public void CaptureUserEvent(
        Guid userId,
        string eventName,
        string plan,
        IReadOnlyDictionary<string, object> properties)
    {
        var distinctId = userId.ToString();

        var eventProperties = new Dictionary<string, object>(properties)
        {
            ["$set"] = new Dictionary<string, object>
            {
                ["plan"] = plan
            },
            ["$unset"] = new[] { "isYearlyPro" }
        };

        var enqueued = postHogClient.Capture(distinctId, eventName, eventProperties);

        LogCaptureEnqueued(logger, eventName, distinctId, enqueued);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "PostHog capture of {EventName} for {DistinctId} enqueued: {Enqueued}")]
    private static partial void LogCaptureEnqueued(ILogger logger, string eventName, string distinctId, bool enqueued);
}
