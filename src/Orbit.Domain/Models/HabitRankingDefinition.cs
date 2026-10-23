using Orbit.Domain.Common;

namespace Orbit.Domain.Models;

/// <summary>A positive recurring-habit ranking over local dates in [DateFrom, DateToExclusive).</summary>
public sealed class HabitRankingDefinition
{
    public const string CompletionLogRecords = "completion_log_records";
    public const int MaxPageSize = 100;
    public const int MaxWindowDays = 3660;
    public const int MaxHabitIds = 100;
    public Guid UserId { get; }
    public DateOnly DateFrom { get; }
    public DateOnly DateToExclusive { get; }
    public string TimeZone { get; }
    public string Metric { get; }
    public IReadOnlyList<Guid> HabitIds { get; }
    public int PageSize { get; }

    private HabitRankingDefinition(Guid userId, DateOnly dateFrom, DateOnly dateToExclusive,
        string timeZone, string metric, IReadOnlyList<Guid> habitIds, int pageSize)
    {
        UserId = userId;
        DateFrom = dateFrom;
        DateToExclusive = dateToExclusive;
        TimeZone = timeZone;
        Metric = metric;
        HabitIds = Array.AsReadOnly(habitIds.Distinct().Order().ToArray());
        PageSize = pageSize;
    }

    public static Result<HabitRankingDefinition> Create(Guid userId, DateOnly dateFrom,
        DateOnly dateToExclusive, string timeZone, string metric, IReadOnlyList<Guid>? habitIds, int pageSize)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(timeZone)
            || dateFrom >= dateToExclusive || dateToExclusive.DayNumber - dateFrom.DayNumber > MaxWindowDays
            || metric != CompletionLogRecords || pageSize is < 1 or > MaxPageSize
            || habitIds is { Count: > MaxHabitIds } || habitIds?.Contains(Guid.Empty) == true)
            return Result.Failure<HabitRankingDefinition>("Invalid habit ranking query.", "HABIT_RANKING_INVALID");

        return Result.Success(new HabitRankingDefinition(userId, dateFrom, dateToExclusive,
            timeZone, metric, habitIds ?? [], pageSize));
    }
}
