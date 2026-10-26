namespace Orbit.Api.Mcp.Tools;

public sealed record PeriodAnalyticsReadRequest(
    DateOnly DateFrom, DateOnly DateTo, string Metric = "positiveLogRecords",
    Guid? HabitId = null, DateOnly? BaselineFrom = null, DateOnly? BaselineTo = null,
    string? Search = null, string? Tag = null, bool? IsBadHabit = null);
