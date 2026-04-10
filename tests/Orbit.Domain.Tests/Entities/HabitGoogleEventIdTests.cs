using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class HabitGoogleEventIdTests
{
    [Fact]
    public void Create_WithGoogleEventId_AssignsProperty()
    {
        var result = Habit.Create(new HabitCreateParams(
            Guid.NewGuid(), "Standup", FrequencyUnit.Day, 1,
            GoogleEventId: "evt_123"));

        result.IsSuccess.Should().BeTrue();
        result.Value.GoogleEventId.Should().Be("evt_123");
    }

    [Fact]
    public void Create_WithoutGoogleEventId_IsNull()
    {
        var result = Habit.Create(new HabitCreateParams(
            Guid.NewGuid(), "Standup", FrequencyUnit.Day, 1));

        result.Value.GoogleEventId.Should().BeNull();
    }

    [Fact]
    public void SetGoogleEventId_UpdatesProperty()
    {
        var habit = Habit.Create(new HabitCreateParams(
            Guid.NewGuid(), "Standup", FrequencyUnit.Day, 1)).Value;

        habit.SetGoogleEventId("evt_456");

        habit.GoogleEventId.Should().Be("evt_456");
    }

    [Fact]
    public void SetGoogleEventId_CanClearId()
    {
        var habit = Habit.Create(new HabitCreateParams(
            Guid.NewGuid(), "Standup", FrequencyUnit.Day, 1,
            GoogleEventId: "evt_123")).Value;

        habit.SetGoogleEventId(null);

        habit.GoogleEventId.Should().BeNull();
    }
}
