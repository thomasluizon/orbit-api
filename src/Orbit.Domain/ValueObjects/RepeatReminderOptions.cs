namespace Orbit.Domain.ValueObjects;

public sealed record RepeatReminderOptions(
    int IntervalMinutes,
    TimeOnly? ActiveStart = null,
    TimeOnly? ActiveEnd = null,
    DateTime? StartAtUtc = null)
{
    public bool IsValid() => IsSupportedInterval(IntervalMinutes)
        && ActiveStart.HasValue == ActiveEnd.HasValue
        && (!ActiveStart.HasValue || ActiveStart != ActiveEnd)
        && (StartAtUtc is null || StartAtUtc.Value.Kind == DateTimeKind.Utc
            && StartAtUtc.Value.Ticks <= DateTime.MaxValue.Ticks - (long)IntervalMinutes * TimeSpan.TicksPerMinute);

    public static bool IsSupportedInterval(int minutes) => minutes is 15 or 20 or 30 or 45
        || minutes is >= 60 and <= 1380 && minutes % 60 == 0;

}
