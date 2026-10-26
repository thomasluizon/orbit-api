using FluentAssertions;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Models;

public class HabitSuggestionDraftTests
{
    private static HabitSuggestionDraft Draft() => new(Guid.NewGuid(), "📖", "Read", "Habit", FrequencyUnit.Day, 1,
        [], new(2026, 10, 26), new(22, 0), new(22, 15), null, [], "A quiet pause before bed.");

    [Fact]
    public void CompleteDraft_UsesHabitEntityGuardsAndKeepsEveryEditableField()
    {
        var draft = Draft() with { Days = [DayOfWeek.Monday], ReminderMinutesBefore = 10, ChecklistItems = ["Choose a book"] };
        var result = draft.ToHabit(Guid.NewGuid());
        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be(draft.Title);
        result.Value.DueTime.Should().Be(draft.DueTime);
        result.Value.DueEndTime.Should().Be(draft.DueEndTime);
        result.Value.Days.Should().Equal(DayOfWeek.Monday);
        result.Value.ReminderTimes.Should().Equal(10);
        result.Value.ChecklistItems.Should().ContainSingle().Which.Text.Should().Be("Choose a book");
    }

    [Fact]
    public void Task_HasNoRecurrence()
    {
        var draft = Draft() with { Type = "Task", FrequencyUnit = null, FrequencyQuantity = null };
        draft.ToHabit(Guid.NewGuid()).Value.Schedule.Should().Be(HabitSchedule.OneTime);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("title")]
    [InlineData("emoji")]
    [InlineData("reason")]
    [InlineData("quantity")]
    [InlineData("time")]
    [InlineData("time_precision")]
    [InlineData("weekday")]
    [InlineData("reminder")]
    [InlineData("checklist")]
    [InlineData("task")]
    public void InvalidDraft_IsRejectedByDomain(string invalid)
    {
        var draft = invalid switch
        {
            "type" => Draft() with { Type = "Unknown" },
            "title" => Draft() with { Title = "" },
            "emoji" => Draft() with { Emoji = "" },
            "reason" => Draft() with { Reason = new string('x', 241) },
            "quantity" => Draft() with { FrequencyQuantity = 0 },
            "time" => Draft() with { DueEndTime = new(21, 0) },
            "time_precision" => Draft() with { DueTime = new(22, 0, 30) },
            "weekday" => Draft() with { Days = [(DayOfWeek)99] },
            "reminder" => Draft() with { ReminderMinutesBefore = -1 },
            "checklist" => Draft() with { ChecklistItems = [""] },
            _ => Draft() with { Type = "Task" }
        };
        draft.ToHabit(Guid.NewGuid()).IsFailure.Should().BeTrue();
    }
}
