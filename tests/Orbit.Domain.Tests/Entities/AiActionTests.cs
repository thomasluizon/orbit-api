using FluentAssertions;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Domain.Tests.Entities;

public class AiActionTests
{
    private static readonly int[] ExpectedReminderTimes = [15, 30];
    private static readonly string[] ExpectedTagNames = ["health", "fitness"];

    [Fact]
    public void Default_Type_IsCreateHabit()
    {
        var action = new AiAction();
        action.Type.Should().Be(AiActionType.CreateHabit);
    }

    [Fact]
    public void AllProperties_CanBeSet()
    {
        var action = new AiAction
        {
            Type = AiActionType.UpdateHabit,
            HabitId = Guid.NewGuid(),
            Title = "Exercise",
            Description = "Daily workout",
            FrequencyUnit = FrequencyUnit.Week,
            FrequencyQuantity = 3,
            Days = new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Friday },
            IsBadHabit = false,
            SlipAlertEnabled = true,
            ReminderEnabled = true,
            ReminderTimes = new List<int> { 15, 30 },
            DueDate = new DateOnly(2025, 6, 15),
            DueTime = new TimeOnly(9, 0),
            Note = "Keep going!",
            SubHabits = new List<AiAction> { new() { Title = "Sub 1" } },
            SuggestedSubHabits = new List<AiAction> { new() { Title = "Suggested 1" } },
            TagNames = new List<string> { "health", "fitness" },
            ChecklistItems = new List<ChecklistItem> { new("Item 1", false) }
        };

        action.Type.Should().Be(AiActionType.UpdateHabit);
        action.HabitId.Should().NotBeNull();
        action.Title.Should().Be("Exercise");
        action.Description.Should().Be("Daily workout");
        action.FrequencyUnit.Should().Be(FrequencyUnit.Week);
        action.FrequencyQuantity.Should().Be(3);
        action.Days.Should().HaveCount(2);
        action.IsBadHabit.Should().BeFalse();
        action.SlipAlertEnabled.Should().BeTrue();
        action.ReminderEnabled.Should().BeTrue();
        action.ReminderTimes.Should().BeEquivalentTo(ExpectedReminderTimes);
        action.DueDate.Should().Be(new DateOnly(2025, 6, 15));
        action.DueTime.Should().Be(new TimeOnly(9, 0));
        action.Note.Should().Be("Keep going!");
        action.SubHabits.Should().HaveCount(1);
        action.SuggestedSubHabits.Should().HaveCount(1);
        action.TagNames.Should().BeEquivalentTo(ExpectedTagNames);
        action.ChecklistItems.Should().HaveCount(1);
    }

    [Fact]
    public void NullableProperties_DefaultToNull()
    {
        var action = new AiAction();

        action.HabitId.Should().BeNull();
        action.Title.Should().BeNull();
        action.Description.Should().BeNull();
        action.FrequencyUnit.Should().BeNull();
        action.FrequencyQuantity.Should().BeNull();
        action.Days.Should().BeNull();
        action.IsBadHabit.Should().BeNull();
        action.SlipAlertEnabled.Should().BeNull();
        action.ReminderEnabled.Should().BeNull();
        action.ReminderTimes.Should().BeNull();
        action.DueDate.Should().BeNull();
        action.DueTime.Should().BeNull();
        action.Note.Should().BeNull();
        action.SubHabits.Should().BeNull();
        action.SuggestedSubHabits.Should().BeNull();
        action.TagNames.Should().BeNull();
        action.ChecklistItems.Should().BeNull();
    }

    [Theory]
    [InlineData(AiActionType.CreateHabit)]
    [InlineData(AiActionType.LogHabit)]
    [InlineData(AiActionType.UpdateHabit)]
    [InlineData(AiActionType.DeleteHabit)]
    [InlineData(AiActionType.SkipHabit)]
    [InlineData(AiActionType.CreateSubHabit)]
    [InlineData(AiActionType.SuggestBreakdown)]
    [InlineData(AiActionType.AssignTags)]
    [InlineData(AiActionType.DuplicateHabit)]
    [InlineData(AiActionType.MoveHabit)]
    public void Type_CanBeSetToAnyActionType(AiActionType type)
    {
        var action = new AiAction { Type = type };
        action.Type.Should().Be(type);
    }

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var action1 = new AiAction { Type = AiActionType.LogHabit, Title = "Test" };
        var action2 = new AiAction { Type = AiActionType.LogHabit, Title = "Test" };
        action1.Should().Be(action2);
    }

    [Fact]
    public void RecordEquality_DifferentValues_AreNotEqual()
    {
        var action1 = new AiAction { Type = AiActionType.LogHabit, Title = "Test" };
        var action2 = new AiAction { Type = AiActionType.DeleteHabit, Title = "Test" };
        action1.Should().NotBe(action2);
    }
}
