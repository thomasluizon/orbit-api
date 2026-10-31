using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Chat;

public sealed partial class PendingOperationChangePreviewerTests
{
    [Theory]
    [InlineData("create_habit", "en", "Every 8 hours", "OneTime")]
    [InlineData("create_habit", "en", "Every 8 hours", "Recurring")]
    [InlineData("bulk_create_habits", "pt-BR", "A cada 8 horas", "General")]
    public async Task Repeat_CreatePreviewShowsTheModelArgumentsBeforeAnyWrite(string operation, string locale, string expected, string schedule)
    {
        var item = schedule switch
        {
            "OneTime" => """{"title":"Reminder","due_date":"2026-09-25","repeat_reminder":{"interval_minutes":480}}""",
            "Recurring" => """{"title":"Reminder","frequency_unit":"Week","frequency_quantity":1,"repeat_reminder":{"interval_minutes":480}}""",
            _ => """{"title":"Reminder","is_general":true,"repeat_reminder":{"interval_minutes":480}}"""
        };
        var args = operation == "create_habit" ? item : "{\"habits\":[" + item + "]}";
        var preview = await Preview(operation, args, new(Locale: locale));
        preview.Should().NotBeNull();
        var field = preview!.Items!.Single().Fields.Single(f => f.Field == "repeat_reminder");
        field.NewValue.Should().Be(expected);
        field.ProposedValue!.Value.GetProperty("interval_minutes").GetInt32().Should().Be(480);
    }

    [Theory]
    [InlineData("update_habit")]
    [InlineData("bulk_update_habits")]
    public async Task Repeat_UpdatePreviewShowsTheOldAndNewClockWithoutMutatingTheHabit(string operation)
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.General, UserId, "Water", null, null,
            new(2026, 9, 25), ReminderEnabled: true, RepeatReminder: new(480))).Value;
        Setup([habit]);
        var args = operation == "update_habit"
            ? $$$"""{"habit_id":"{{{habit.Id}}}","repeat_reminder":{"interval_minutes":420}}"""
            : """{"filter":{"all":true},"updates":{"repeat_reminder":{"interval_minutes":420}}}""";
        var preview = await Preview(operation, args, new(Locale: "pt-BR"));
        preview.Should().NotBeNull();
        var field = preview!.Items!.Single().Fields.Single(f => f.Field == "repeat_reminder");
        field.OldValue.Should().StartWith("A cada 8 horas");
        field.NewValue.Should().StartWith("A cada 7 horas");
        field.ProposedValue!.Value.GetProperty("interval_minutes").GetInt32().Should().Be(420);
        habit.RepeatIntervalMinutes.Should().Be(480);
    }
}
