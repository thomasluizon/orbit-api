using BenchmarkDotNet.Attributes;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;

namespace Orbit.Benchmarks;

/// <summary>
/// <see cref="RetrospectiveMetricsCalculator.Compute"/> — the deterministic weekly/monthly retrospective
/// rollup (completion rates, weekday consistency, top and needs-attention lists) — over a twenty-habit
/// portfolio across a ninety-day window.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[JsonExporterAttribute.Full]
public class RetrospectiveMetricsBenchmarks
{
    private List<Habit> _habits = null!;
    private DateOnly _dateFrom;
    private DateOnly _dateTo;

    [GlobalSetup]
    public void Setup()
    {
        _dateTo = new DateOnly(2026, 7, 1);
        _dateFrom = _dateTo.AddDays(-89);
        _habits = BenchmarkFixtures.HabitPortfolio(_dateFrom, _dateTo, habitCount: 20);
    }

    [Benchmark]
    public RetrospectiveMetrics Compute_TwentyHabitsNinetyDays() =>
        RetrospectiveMetricsCalculator.Compute(_habits, _dateFrom, _dateTo, currentStreak: 12, bestStreak: 30);
}
