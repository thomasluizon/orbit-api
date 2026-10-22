using System.Text.Json.Serialization;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

[JsonDerivedType(typeof(HabitListCard))]
[JsonDerivedType(typeof(GoalListCard))]
[JsonDerivedType(typeof(MetricsCard))]
[JsonDerivedType(typeof(PeriodInsightCard))]
[JsonDerivedType(typeof(DaySummaryCard))]
[JsonDerivedType(typeof(StreakCard))]
[JsonDerivedType(typeof(CalendarCard))]
[JsonDerivedType(typeof(RecordListCard))]
[JsonDerivedType(typeof(AccountRowsCard))]
[JsonDerivedType(typeof(PeriodTotalCard))]
[JsonDerivedType(typeof(PeriodComparisonCard))]
public abstract record ReadBlockCard;

public sealed record ReadBlockRefreshResponse(string Kind, ReadBlockCard Block, ReadBlockMetadata Read)
{
    public static ReadBlockRefreshResponse From(QueryReadBlock read) => read.Block is ReadBlockCard card
        ? new(read.Kind, card, read.Read)
        : throw new InvalidOperationException("A read block must carry a declared read card.");
}
