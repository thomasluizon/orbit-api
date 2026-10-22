namespace Orbit.Api.Mcp.Tools;

public sealed record HabitRankingReadRequest(DateOnly? DateFrom = null, DateOnly? DateToExclusive = null,
    string? Window = null, int? Months = null, string? Metric = null, IReadOnlyList<Guid>? HabitIds = null,
    int? PageSize = null, string? QueryToken = null, string? Cursor = null, bool Refresh = false);
