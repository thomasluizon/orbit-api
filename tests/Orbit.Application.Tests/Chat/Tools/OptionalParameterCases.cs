using System.Runtime.CompilerServices;
using System.Text.Json;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Entities;

namespace Orbit.Application.Tests.Chat.Tools;

public static class OptionalParameterCases
{
    public static IEnumerable<object[]> Fields(Type toolType, string path)
    {
        var tool = (IAiTool)RuntimeHelpers.GetUninitializedObject(toolType);
        var schema = JsonSerializer.SerializeToElement(tool.GetParameterSchema());
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            schema = segment == "items" ? schema.GetProperty("items") : schema.GetProperty("properties").GetProperty(segment);
        var required = schema.TryGetProperty("required", out var names)
            ? names.EnumerateArray().Select(name => name.GetString()).ToHashSet() : [];
        return schema.GetProperty("properties").EnumerateObject()
            .Where(property => !required.Contains(property.Name)).Select(property => new object[] { property.Name }).ToArray();
    }

    public static string HabitState(Habit habit) => JsonSerializer.Serialize(new
    {
        habit.Title,
        habit.Description,
        habit.Emoji,
        habit.FrequencyUnit,
        habit.FrequencyQuantity,
        habit.IntervalWeeks,
        habit.Days,
        habit.DueDate,
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
