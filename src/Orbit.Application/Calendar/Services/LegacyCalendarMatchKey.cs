namespace Orbit.Application.Calendar.Services;

internal static class LegacyCalendarMatchKey
{
    /// <summary>
    /// Identifies a legacy calendar event by its normalized title, start date, and start time.
    /// Auto-sync reconciliation and suggestion filtering must share this key to recognise the
    /// same imported habit and avoid offering it again.
    /// </summary>
    internal static string BuildLegacyMatchKey(string title, string? startDate, string? startTime)
    {
        return $"{title.Trim().ToLowerInvariant()}|{startDate ?? ""}|{startTime ?? ""}";
    }
}
