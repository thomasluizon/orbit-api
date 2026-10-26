namespace Orbit.Domain.Models;

public sealed record ReadWindow
{
    public DateOnly From { get; }
    public DateOnly To { get; }

    public ReadWindow(DateOnly from, DateOnly to)
    {
        if (to < from || to.DayNumber - from.DayNumber > 3659)
            throw new ArgumentException("A read window must contain between one and 3660 days.");
        From = from;
        To = to;
    }
}

public sealed record PeriodHabitMetrics(
    ReadWindow Window, int PositiveLogRecords, int DistinctLoggedDates,
    int MetScheduledDates, int ScheduledDates, decimal? CompletionRate,
    int CurrentStreak, int BestStreak, string StreakScope = "period", int? CompletedHabits = null);

public sealed record PeriodMetricBucket(DateOnly StartDate, DateOnly EndDate, int Scheduled, int Completed, int? CompletionRate);
public sealed record PeriodMetricSeries(string Granularity, IReadOnlyList<PeriodMetricBucket> Points);
