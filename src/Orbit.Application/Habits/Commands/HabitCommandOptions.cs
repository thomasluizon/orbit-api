using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Habits.Commands;

/// <summary>
/// Shared optional settings for Create/CreateSubHabit commands,
/// grouping fields that would otherwise push constructor params beyond 7 (S107).
/// </summary>
public record HabitCommandOptions(
    IReadOnlyList<DayOfWeek>? Days = null,
    TimeOnly? DueTime = null,
    TimeOnly? DueEndTime = null,
    bool ReminderEnabled = false,
    IReadOnlyList<int>? ReminderTimes = null,
    bool SlipAlertEnabled = false,
    IReadOnlyList<ChecklistItem>? ChecklistItems = null,
    IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null,
    DateOnly? EndDate = null,
    bool IsFlexible = false);

/// <summary>
/// Optional settings for UpdateHabitCommand. Uses nullable booleans
/// to distinguish "not provided" from "set to false".
/// </summary>
public record UpdateHabitCommandOptions(
    IReadOnlyList<DayOfWeek>? Days = null,
    TimeOnly? DueTime = null,
    TimeOnly? DueEndTime = null,
    bool? ReminderEnabled = null,
    IReadOnlyList<int>? ReminderTimes = null,
    bool? SlipAlertEnabled = null,
    IReadOnlyList<ChecklistItem>? ChecklistItems = null,
    IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null,
    DateOnly? EndDate = null,
    bool? IsFlexible = null);
