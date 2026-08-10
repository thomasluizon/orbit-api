using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Bound when no PostHog project key is configured: every capture is discarded before it reaches
/// the network, so an environment that has not opted in produces no outbound analytics traffic.
/// </summary>
public sealed class NoOpProductAnalytics : IProductAnalytics
{
    public void CaptureUserEvent(Guid userId, string eventName, string plan)
    {
    }
}
