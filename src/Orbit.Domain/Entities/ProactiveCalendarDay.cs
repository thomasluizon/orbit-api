using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

/// <summary>A durable read claim prevents repeated calendar fetches across hourly runs and processes.</summary>
public sealed class ProactiveCalendarDay : Entity
{
    public Guid UserId { get; private set; }
    public DateOnly LocalDate { get; private set; }
    public int? TimedEventCount { get; private set; }
    public int? TimedMinutes { get; private set; }

    private ProactiveCalendarDay() { }

    public static ProactiveCalendarDay Claim(Guid userId, DateOnly localDate)
    {
        if (userId == Guid.Empty || localDate == default) throw new ArgumentException("User and local date are required.");
        return new ProactiveCalendarDay { UserId = userId, LocalDate = localDate };
    }

    public void Complete(int timedEventCount, int timedMinutes)
    {
        if (timedEventCount < 0 || timedMinutes < 0) throw new ArgumentOutOfRangeException(nameof(timedEventCount));
        TimedEventCount = timedEventCount;
        TimedMinutes = timedMinutes;
    }
}
