using Microsoft.Extensions.Logging;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Common;

/// <summary>
/// Wraps every product-analytics capture so telemetry can never fail the business operation that
/// produced it. This is a sanctioned boundary swallow: the Stripe webhook contract and the auth flow
/// outrank analytics, and a capture failure must not turn into a retried webhook or a failed login.
/// </summary>
internal static partial class AnalyticsCapture
{
    public static void SafeCaptureUserEvent(
        IProductAnalytics analytics,
        ILogger logger,
        User user,
        string eventName,
        IReadOnlyDictionary<string, object>? properties = null)
    {
        try
        {
            analytics.CaptureUserEvent(user.Id, eventName, user.Plan.ToString(), properties);
        }
        catch (Exception ex)
        {
            LogCaptureFailed(logger, ex, eventName, user.Id);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Product analytics capture of {EventName} failed for user {UserId}")]
    private static partial void LogCaptureFailed(ILogger logger, Exception ex, string eventName, Guid userId);
}
