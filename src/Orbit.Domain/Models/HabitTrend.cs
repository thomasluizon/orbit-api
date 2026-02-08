namespace Orbit.Domain.Models;

public record TrendPoint(
    string Period,
    decimal Average,
    decimal Minimum,
    decimal Maximum,
    int Count);

public record HabitTrend(
    IReadOnlyList<TrendPoint> Weekly,
    IReadOnlyList<TrendPoint> Monthly);
