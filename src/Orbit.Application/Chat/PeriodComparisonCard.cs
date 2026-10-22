using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public sealed record PeriodComparisonCard(
    string Metric, string Unit, PeriodHabitMetrics Current, PeriodHabitMetrics Baseline,
    int CurrentValue, int BaselineValue, int AbsoluteDelta, decimal? RelativeDelta,
    Guid? HabitId = null, ReadBlockMetadata? Read = null) : ReadBlockCard;
