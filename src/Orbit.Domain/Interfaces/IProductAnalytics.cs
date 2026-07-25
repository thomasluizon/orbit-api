namespace Orbit.Domain.Interfaces;

/// <summary>
/// Captures one product-analytics event for a user, keyed on the Orbit user id.
/// Fire-and-forget: implementations enqueue and return immediately, and callers treat a
/// capture failure as telemetry loss, never as a failure of the business operation.
/// </summary>
public interface IProductAnalytics
{
    void CaptureUserEvent(Guid userId, string eventName, string plan, bool isYearlyPro);
}
