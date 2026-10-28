using Orbit.Domain.Enums;

namespace Orbit.Domain.ValueObjects;

public sealed record RepeatReminderOptions(
    int IntervalMinutes,
    TimeOnly? ActiveStart = null,
    TimeOnly? ActiveEnd = null,
    DateTime? StartAtUtc = null)
{
    public bool IsValid => IsSupportedInterval(IntervalMinutes)
        && ActiveStart.HasValue == ActiveEnd.HasValue
        && (!ActiveStart.HasValue || ActiveStart != ActiveEnd)
        && (StartAtUtc is null || StartAtUtc.Value.Kind == DateTimeKind.Utc);

    public static bool IsSupportedInterval(int minutes) => minutes is 15 or 20 or 30 or 45
        || minutes is >= 60 and <= 1380 && minutes % 60 == 0;

    public static bool SupportsSchedule(HabitSchedule schedule, FrequencyUnit? unit, int? quantity,
        IEnumerable<DayOfWeek>? days, int? intervalWeeks) =>
        (intervalWeeks ?? 1) == 1 && (days is null || !days.Any() || days.Distinct().Count() == 7)
        && (schedule == HabitSchedule.General
            || unit == FrequencyUnit.Day && (schedule == HabitSchedule.Flexible
                || schedule == HabitSchedule.Recurring && quantity == 1));
}
