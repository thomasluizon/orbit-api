using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Infrastructure.Services;

internal sealed class SchedulerHabit : IHabitSchedule
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateOnly DueDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateOnly? ScheduledStartDate { get; set; }
    public FrequencyUnit? FrequencyUnit { get; set; }
    public int? FrequencyQuantity { get; set; }
    public int? IntervalWeeks { get; set; }
    public bool IsFlexible { get; set; }
    public ICollection<DayOfWeek> Days { get; set; } = [];
    public TimeOnly? DueTime { get; set; }
    public IReadOnlyList<int> ReminderTimes { get; set; } = [];
    public IReadOnlyList<ScheduledReminderTime> ScheduledReminders { get; set; } = [];
    public IReadOnlyList<RelativeReminderTime> RelativeReminders { get; set; } = [];
}

internal sealed class SchedulerUser
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? TimeZone { get; set; }
    public string? Language { get; set; }
    public int WeekStartDay { get; set; }
    public int CurrentStreak { get; set; }
}
