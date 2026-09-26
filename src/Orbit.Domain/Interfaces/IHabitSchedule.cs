using Orbit.Domain.Enums;

namespace Orbit.Domain.Interfaces;

public interface IHabitSchedule
{
    DateOnly DueDate { get; }
    DateOnly? EndDate { get; }
    DateOnly? ScheduledStartDate { get; }
    FrequencyUnit? FrequencyUnit { get; }
    int? FrequencyQuantity { get; }
    int? IntervalWeeks { get; }
    bool IsFlexible { get; }
    ICollection<DayOfWeek> Days { get; }
}
