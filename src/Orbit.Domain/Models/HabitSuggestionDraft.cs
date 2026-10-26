using System.Text.Json.Serialization;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Domain.Models;

public sealed record HabitSuggestionDraft(
    Guid Id, string Emoji, string Title, string Type,
    FrequencyUnit? FrequencyUnit, int? FrequencyQuantity, IReadOnlyList<DayOfWeek> Days,
    DateOnly DueDate, TimeOnly DueTime, TimeOnly DueEndTime,
    int? ReminderMinutesBefore, IReadOnlyList<string> ChecklistItems, string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ExistingHabitId = null)
{
    public Result<Habit> ToHabit(Guid userId)
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Emoji) || Emoji.Length > 10
            || string.IsNullOrWhiteSpace(Reason) || Reason.Length > 240 || Reason.Contains('\n')
            || !HasValidSchedule() || ChecklistItems is null
            || DueDate == default || DueEndTime <= DueTime || ExistingHabitId == Guid.Empty
            || DueTime.Ticks % TimeSpan.TicksPerMinute != 0 || DueEndTime.Ticks % TimeSpan.TicksPerMinute != 0
            || ReminderMinutesBefore is < 0 or > 1440
            || ChecklistItems.Any(string.IsNullOrWhiteSpace))
            return Result.Failure<Habit>("Invalid habit suggestion.", "HABIT_SUGGESTION_INVALID");

        return Habit.Create(new HabitCreateParams(
            Type == "Task" ? HabitSchedule.OneTime : HabitSchedule.Recurring,
            userId, Title, FrequencyUnit, FrequencyQuantity, DueDate,
            Days: Days, DueTime: DueTime, DueEndTime: DueEndTime,
            Emoji: Emoji, ReminderEnabled: ReminderMinutesBefore.HasValue,
            ReminderTimes: ReminderMinutesBefore is { } minutes ? [minutes] : [],
            ChecklistItems: ChecklistItems.Select(text => new ChecklistItem(text, false)).ToArray()));
    }

    private bool HasValidSchedule() => Days is not null && Type is "Task" or "Habit"
        && (Type == "Task" ? FrequencyUnit is null && FrequencyQuantity is null && Days.Count == 0
            : FrequencyUnit.HasValue && FrequencyQuantity.HasValue)
        && Days.All(Enum.IsDefined) && Days.Distinct().Count() == Days.Count
        && (Days.Count == 0 || FrequencyUnit == Enums.FrequencyUnit.Day && FrequencyQuantity == 1);
}

public sealed record HabitSuggestionsCard(string Language, IReadOnlyList<HabitSuggestionDraft> Drafts);

public sealed record HabitSuggestionRequest(Guid UserId, string Instruction, string Language, int Count,
    DateOnly Today, IReadOnlyList<Habit> Habits, IReadOnlyList<Goal> Goals,
    HabitSuggestionDraft? Original = null, string? Correction = null, int WeekStartDay = 1);
