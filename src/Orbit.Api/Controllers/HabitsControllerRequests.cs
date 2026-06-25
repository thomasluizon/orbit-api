using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Api.Controllers;

// [Authorize] is declared on the primary HabitsController partial; repeating it here would be a
// duplicate non-multiple attribute: https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-messages/cs0579
public partial class HabitsController
{
    public record CreateHabitRequest(
        string Title,
        string? Description,
        FrequencyUnit? FrequencyUnit,
        int? FrequencyQuantity,
        IReadOnlyList<System.DayOfWeek>? Days = null,
        bool IsBadHabit = false,
        IReadOnlyList<string>? SubHabits = null,
        DateOnly? DueDate = null,
        TimeOnly? DueTime = null,
        TimeOnly? DueEndTime = null,
        bool ReminderEnabled = false,
        IReadOnlyList<int>? ReminderTimes = null,
        IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null,
        bool SlipAlertEnabled = false,
        IReadOnlyList<Guid>? TagIds = null,
        IReadOnlyList<ChecklistItem>? ChecklistItems = null,
        bool IsGeneral = false,
        DateOnly? EndDate = null,
        bool IsFlexible = false,
        IReadOnlyList<Guid>? GoalIds = null,
        string? Emoji = null);

    public record UpdateHabitRequest(
        string Title,
        string? Description,
        FrequencyUnit? FrequencyUnit,
        int? FrequencyQuantity,
        IReadOnlyList<System.DayOfWeek>? Days = null,
        bool IsBadHabit = false,
        DateOnly? DueDate = null,
        TimeOnly? DueTime = null,
        TimeOnly? DueEndTime = null,
        bool? ReminderEnabled = null,
        IReadOnlyList<int>? ReminderTimes = null,
        IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null,
        bool? SlipAlertEnabled = null,
        IReadOnlyList<ChecklistItem>? ChecklistItems = null,
        bool? IsGeneral = null,
        DateOnly? EndDate = null,
        bool? ClearEndDate = null,
        bool? IsFlexible = null,
        IReadOnlyList<Guid>? GoalIds = null,
        string? Emoji = null);

    public record UpdateChecklistRequest(IReadOnlyList<ChecklistItem> ChecklistItems);

    public record LogHabitRequest(DateOnly? Date = null);

    public record SkipHabitRequest(DateOnly? Date = null);

    public record BulkCreateHabitsRequest(
        IReadOnlyList<BulkHabitItemRequest> Habits,
        bool FromSyncReview = false);

    public record BulkHabitItemRequest(
        string Title,
        string? Description,
        FrequencyUnit? FrequencyUnit,
        int? FrequencyQuantity,
        IReadOnlyList<System.DayOfWeek>? Days = null,
        bool IsBadHabit = false,
        DateOnly? DueDate = null,
        TimeOnly? DueTime = null,
        TimeOnly? DueEndTime = null,
        bool ReminderEnabled = false,
        IReadOnlyList<int>? ReminderTimes = null,
        IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null,
        IReadOnlyList<BulkHabitItemRequest>? SubHabits = null,
        bool IsGeneral = false,
        DateOnly? EndDate = null,
        bool IsFlexible = false,
        IReadOnlyList<ChecklistItem>? ChecklistItems = null,
        string? GoogleEventId = null,
        string? Emoji = null);

    public record BulkDeleteHabitsRequest(IReadOnlyList<Guid> HabitIds);

    public record BulkLogHabitItem(Guid HabitId, DateOnly? Date = null);
    public record BulkLogHabitsRequest(IReadOnlyList<BulkLogHabitItem> Items);

    public record BulkSkipHabitItem(Guid HabitId, DateOnly? Date = null);
    public record BulkSkipHabitsRequest(IReadOnlyList<BulkSkipHabitItem> Items);

    public record ReorderHabitsRequest(IReadOnlyList<HabitPositionRequest> Positions);

    public record HabitPositionRequest(Guid HabitId, int Position);

    public record MoveHabitParentRequest(Guid? ParentId);

    public record GetHabitsFilterRequest
    {
        public DateOnly? DateFrom { get; init; }
        public DateOnly? DateTo { get; init; }
        public bool? IncludeOverdue { get; init; }
        public string? Search { get; init; }
        public string? FrequencyUnit { get; init; }
        public bool? IsCompleted { get; init; }
        public Guid[]? TagIds { get; init; }
        public bool? IsGeneral { get; init; }
        public int Page { get; init; } = 1;
        public int PageSize { get; init; } = 50;
        public bool? IncludeGeneral { get; init; }
    }

    public record CreateSubHabitRequest(
        string Title,
        string? Description = null,
        FrequencyUnit? FrequencyUnit = null,
        int? FrequencyQuantity = null,
        IReadOnlyList<System.DayOfWeek>? Days = null,
        bool IsBadHabit = false,
        DateOnly? DueDate = null,
        TimeOnly? DueTime = null,
        TimeOnly? DueEndTime = null,
        bool ReminderEnabled = false,
        IReadOnlyList<int>? ReminderTimes = null,
        IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null,
        bool SlipAlertEnabled = false,
        IReadOnlyList<ChecklistItem>? ChecklistItems = null,
        IReadOnlyList<Guid>? TagIds = null,
        DateOnly? EndDate = null,
        bool IsFlexible = false,
        string? Emoji = null);

    public record LinkGoalsRequest(List<Guid> GoalIds);
}
