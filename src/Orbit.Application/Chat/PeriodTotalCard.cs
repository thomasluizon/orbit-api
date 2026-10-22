using Orbit.Domain.Models;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Chat;

public sealed record PeriodTotalCard(
    string Metric, string Unit, int Value, PeriodHabitMetrics Metrics,
    CompletionSeries Series, Guid? HabitId = null, ReadBlockMetadata? Read = null) : ReadBlockCard;
