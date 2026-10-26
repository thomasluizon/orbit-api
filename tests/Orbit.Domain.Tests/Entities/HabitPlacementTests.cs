using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Domain.Tests.Entities;

public sealed class HabitPlacementTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    [InlineData("")]
    public void Placement_RejectsUnknownValues(string? value) =>
        HabitPlacement.Validate(value, Guid.NewGuid()).IsFailure.Should().BeTrue();

    [Theory]
    [InlineData(HabitPlacement.SubHabit)]
    [InlineData(HabitPlacement.ChecklistItem)]
    public void Placement_RequiresDestination(string value) =>
        HabitPlacement.Validate(value, null).IsFailure.Should().BeTrue();

    [Fact]
    public void NewParent_HasNoDestinationRequirement() =>
        HabitPlacement.Validate(HabitPlacement.NewParent, null).IsSuccess.Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Append_InvalidTextDoesNotMutateHabit(string? text)
    {
        var habit = Create();
        habit.AppendChecklistItem(text!).IsFailure.Should().BeTrue();
        habit.ChecklistItems.Should().BeEmpty();
    }

    [Fact]
    public void Append_EnforcesTextLengthAndCapacity()
    {
        var habit = Create();
        habit.AppendChecklistItem(new string('a', DomainConstants.MaxChecklistItemTextLength + 1)).IsFailure.Should().BeTrue();
        for (var index = 0; index < DomainConstants.MaxChecklistItems; index++)
            habit.AppendChecklistItem($"Item {index}").IsSuccess.Should().BeTrue();
        var before = habit.ChecklistItems.ToArray();
        habit.AppendChecklistItem("One more").IsFailure.Should().BeTrue();
        habit.ChecklistItems.Should().Equal(before);
    }

    private static Habit Create() => Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
        Guid.NewGuid(), "Morning routine", FrequencyUnit.Day, 1, new DateOnly(2026, 10, 1))).Value;
}
