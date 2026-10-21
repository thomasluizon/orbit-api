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
    public void CaptureAggregateEvent(string eventName, IReadOnlyDictionary<string, object> properties)
    {
        var distinctId = eventName == "$ai_generation" ? "aggregate:astra_ai" : "aggregate:astra_crisis_resources";
        var enqueued = postHogClient.Capture(
            distinctId,
            eventName,
            new Dictionary<string, object>(properties));
        LogCaptureResult(eventName, distinctId, enqueued);
    }

    public void CaptureUserEvent(
        Guid userId,
        string eventName,
        string? plan,
        IReadOnlyDictionary<string, object>? properties = null)
    {
        var distinctId = userId.ToString();

        var eventProperties = properties is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(properties);
        if (plan is not null)
            eventProperties["$set"] = new Dictionary<string, object> { ["plan"] = plan };
        eventProperties["$unset"] = new[] { "isYearlyPro" };

        var enqueued = postHogClient.Capture(distinctId, eventName, eventProperties);

        LogCaptureResult(eventName, distinctId, enqueued);
    }

    private void LogCaptureResult(string eventName, string distinctId, bool enqueued)
    {
        if (enqueued)
            LogCaptureEnqueued(logger, eventName, distinctId, enqueued);
        else
            LogCaptureRejected(logger, eventName, distinctId);
    }

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "PostHog queue rejected {EventName} for {DistinctId}")]
    private static partial void LogCaptureRejected(ILogger logger, string eventName, string distinctId);

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "PostHog capture of {EventName} for {DistinctId} enqueued: {Enqueued}")]
    private static partial void LogCaptureEnqueued(ILogger logger, string eventName, string distinctId, bool enqueued);
}
