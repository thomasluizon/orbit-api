using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class HabitTagUpdateTests
{
    [Theory]
    [InlineData("foreign")]
    [InlineData("deleted")]
    [InlineData("cap")]
    [InlineData("title")]
    public void UpdateWithTags_InvalidAssignmentOrFields_PreservesBoth(string kind)
    {
        var userId = Guid.NewGuid();
        var habit = Habit.Create(new(userId, "Original", FrequencyUnit.Day, 1, new DateOnly(2026, 4, 3))).Value;
        var original = Tag.Create(userId, "Original", "#ffffff").Value;
        habit.AddTag(original);
        var requested = Tag.Create(kind == "foreign" ? Guid.NewGuid() : userId, "Requested", "#ffffff").Value;
        if (kind == "deleted") requested.SoftDelete();
        var update = new HabitUpdateParams(kind == "title" ? "" : "Changed", null, FrequencyUnit.Day, 1, null, false, null);

        var result = habit.Update(update, [requested], kind == "cap" ? 0 : 5);

        result.IsFailure.Should().BeTrue();
        habit.Title.Should().Be("Original");
        habit.Tags.Should().Equal(original);
    }

    [Fact]
    public void UpdateWithTags_ReplacesAsSetAndNullPreservesWhileEmptyClears()
    {
        var userId = Guid.NewGuid();
        var habit = Habit.Create(new(userId, "Original", FrequencyUnit.Day, 1, new DateOnly(2026, 4, 3))).Value;
        var tag = Tag.Create(userId, "Selected", "#ffffff").Value;
        var update = new HabitUpdateParams("Changed", null, FrequencyUnit.Day, 1, null, false, null);
        habit.Update(update, [tag, tag], 5).IsSuccess.Should().BeTrue();
        habit.Tags.Should().Equal(tag);
        habit.Update(update, null, 5).IsSuccess.Should().BeTrue();
        habit.Tags.Should().Equal(tag);
        habit.Update(update, [], 5).IsSuccess.Should().BeTrue();
        habit.Tags.Should().BeEmpty();
    }
}
