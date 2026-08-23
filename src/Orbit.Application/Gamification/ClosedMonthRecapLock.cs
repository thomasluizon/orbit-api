namespace Orbit.Application.Gamification;

/// <summary>
/// Provides the account scoped transaction lock shared by reconciliation, closed month recap creation,
/// and account reset, so reset cannot race persisted state derived from data it erases.
/// </summary>
public static class ClosedMonthRecapLock
{
    public static string ForUser(Guid userId) => $"closed-month-recap:{userId}";
}
