using BenchmarkDotNet.Attributes;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Benchmarks;

/// <summary>
/// <see cref="HabitMetricsCalculator.Calculate"/> — the per-habit streak/completion-rate rollup rendered
/// on every habit detail screen — over a daily habit with a year of completion logs.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[JsonExporterAttribute.Full]
public class HabitMetricsBenchmarks
{
    private Habit _habit = null!;
    private DateOnly _today;

    [GlobalSetup]
    public void Setup()
    {
        _today = new DateOnly(2026, 7, 1);
        _habit = BenchmarkFixtures.DailyHabitWithHistory(_today.AddDays(-365), _today, completeEveryNthDay: 2);
    }

    [Benchmark]
    public HabitMetrics Calculate_OneYearDailyHabit() =>
        HabitMetricsCalculator.Calculate(_habit, _today, TimeZoneInfo.Utc);
}
