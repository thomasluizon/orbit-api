using System.Text.Json;
using Orbit.Domain.Entities;

namespace Orbit.Application.Tests.Chat.Tools;

public static partial class OptionalParameterCases
{
    /// <summary>Discovery uses reviewed metadata; Fields_MatchLiveSchema validates it inside test coverage.</summary>
    public static IEnumerable<object[]> Fields(Type toolType, string path) =>
        OptionalFields[(toolType, path)].Select(field => new object[] { field }).ToArray();

    public static IEnumerable<object[]> Schemas() =>
        OptionalFields.Keys.Select(key => new object[] { key.ToolType, key.Path }).ToArray();

    public static string HabitState(Habit habit) => JsonSerializer.Serialize(new
    {
        habit.Title,
        habit.Description,
        habit.Emoji,
        habit.FrequencyUnit,
        habit.FrequencyQuantity,
        habit.IntervalWeeks,
        habit.Days,
        habit.NextDueDate,
        habit.EndDate,
        habit.DueTime,
        habit.DueEndTime,
        habit.IsBadHabit,
        habit.IsGeneral,
        habit.IsFlexible,
        habit.ReminderEnabled,
        habit.ReminderTimes,
        habit.ChecklistItems,
        habit.ScheduledReminders,
        habit.RelativeReminders,
        habit.SlipAlertEnabled
    });
}
