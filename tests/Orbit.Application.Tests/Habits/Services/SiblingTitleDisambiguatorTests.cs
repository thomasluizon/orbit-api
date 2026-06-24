using FluentAssertions;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Habits.Services;

public class SiblingTitleDisambiguatorTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static Habit CreateHabit(string title) =>
        Habit.Create(new HabitCreateParams(UserId, title, FrequencyUnit.Day, 1, DueDate: Today)).Value;

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        var suffixes = SiblingTitleDisambiguator.ComputeSuffixes([]);

        suffixes.Should().BeEmpty();
    }

    [Fact]
    public void NoDuplicates_ReturnsEmpty()
    {
        var habits = new List<Habit> { CreateHabit("Water"), CreateHabit("Read"), CreateHabit("Run") };

        var suffixes = SiblingTitleDisambiguator.ComputeSuffixes(habits);

        suffixes.Should().BeEmpty();
    }

    [Fact]
    public void IdenticalSiblings_AreNumberedInInputOrder()
    {
        var first = CreateHabit("Water - 710ml");
        var second = CreateHabit("Water - 710ml");
        var third = CreateHabit("Water - 710ml");

        var suffixes = SiblingTitleDisambiguator.ComputeSuffixes([first, second, third]);

        suffixes[first.Id].Should().Be(" (1 of 3)");
        suffixes[second.Id].Should().Be(" (2 of 3)");
        suffixes[third.Id].Should().Be(" (3 of 3)");
    }

    [Fact]
    public void OnlyDuplicatedTitles_AreNumbered()
    {
        var unique = CreateHabit("Read");
        var duplicateA = CreateHabit("Water");
        var duplicateB = CreateHabit("Water");

        var suffixes = SiblingTitleDisambiguator.ComputeSuffixes([unique, duplicateA, duplicateB]);

        suffixes.Should().NotContainKey(unique.Id);
        suffixes[duplicateA.Id].Should().Be(" (1 of 2)");
        suffixes[duplicateB.Id].Should().Be(" (2 of 2)");
    }

    [Fact]
    public void TitleMatching_IsCaseSensitive()
    {
        var lower = CreateHabit("water");
        var upper = CreateHabit("Water");

        var suffixes = SiblingTitleDisambiguator.ComputeSuffixes([lower, upper]);

        suffixes.Should().BeEmpty();
    }

    [Fact]
    public void MultipleDuplicateGroups_AreNumberedIndependently()
    {
        var waterA = CreateHabit("Water");
        var read = CreateHabit("Read");
        var waterB = CreateHabit("Water");
        var stretchA = CreateHabit("Stretch");
        var stretchB = CreateHabit("Stretch");

        var suffixes = SiblingTitleDisambiguator.ComputeSuffixes([waterA, read, waterB, stretchA, stretchB]);

        suffixes[waterA.Id].Should().Be(" (1 of 2)");
        suffixes[waterB.Id].Should().Be(" (2 of 2)");
        suffixes[stretchA.Id].Should().Be(" (1 of 2)");
        suffixes[stretchB.Id].Should().Be(" (2 of 2)");
        suffixes.Should().NotContainKey(read.Id);
    }
}
