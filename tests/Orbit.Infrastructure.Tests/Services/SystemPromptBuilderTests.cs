using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class SystemPromptBuilderTests
{
    private static readonly Guid TestUserId = Guid.NewGuid();

    [Fact]
    public void Build_NoHabits_ContainsNoneMarker()
    {
        // Arrange
        var habits = Array.Empty<Habit>();
        var facts = Array.Empty<UserFact>();

        // Act
        var result = SystemPromptBuilder.BuildSystemPrompt(habits, facts);

        // Assert
        result.Should().Contain("(none)");
    }

    [Fact]
    public void Build_WithHabits_ListsHabitTitle()
    {
        // Arrange
        var habit = Habit.Create(TestUserId, "Morning Run", FrequencyUnit.Day, 1).Value;
        var habits = new[] { habit };
        var facts = Array.Empty<UserFact>();

        // Act
        var result = SystemPromptBuilder.BuildSystemPrompt(habits, facts);

        // Assert
        result.Should().Contain("Morning Run");
    }

    [Fact]
    public void Build_WithMetrics_IncludesStreakInfo()
    {
        // Arrange
        var habit = Habit.Create(TestUserId, "Meditation", FrequencyUnit.Day, 1).Value;
        var habits = new[] { habit };
        var facts = Array.Empty<UserFact>();
        var metrics = new Dictionary<Guid, HabitMetrics>
        {
            [habit.Id] = new HabitMetrics(
                CurrentStreak: 5,
                LongestStreak: 10,
                WeeklyCompletionRate: 80m,
                MonthlyCompletionRate: 75m,
                TotalCompletions: 30,
                LastCompletedDate: DateOnly.FromDateTime(DateTime.UtcNow))
        };

        // Act
        var result = SystemPromptBuilder.BuildSystemPrompt(
            habits, facts, habitMetrics: metrics);

        // Assert
        result.Should().Contain("streak: 5d");
        result.Should().Contain("best: 10d");
        result.Should().Contain("total: 30");
    }

    [Fact]
    public void Build_WithUserFacts_GroupsByCategory()
    {
        // Arrange
        var habits = Array.Empty<Habit>();
        var preferenceFact = UserFact.Create(TestUserId, "Likes outdoor activities", "preference").Value;
        var routineFact = UserFact.Create(TestUserId, "Wakes up at 7am", "routine").Value;
        var facts = new[] { preferenceFact, routineFact };

        // Act
        var result = SystemPromptBuilder.BuildSystemPrompt(habits, facts);

        // Assert
        result.Should().Contain("**Preferences**");
        result.Should().Contain("Likes outdoor activities");
        result.Should().Contain("**Routines**");
        result.Should().Contain("Wakes up at 7am");
    }

    [Fact]
    public void Build_NoFacts_ContainsNothingYet()
    {
        // Arrange
        var habits = Array.Empty<Habit>();
        var facts = Array.Empty<UserFact>();

        // Act
        var result = SystemPromptBuilder.BuildSystemPrompt(habits, facts);

        // Assert
        result.Should().Contain("(nothing yet");
    }

    [Fact]
    public void Build_WithImage_IncludesImageInstructions()
    {
        // Arrange
        var habits = Array.Empty<Habit>();
        var facts = Array.Empty<UserFact>();

        // Act
        var result = SystemPromptBuilder.BuildSystemPrompt(habits, facts, hasImage: true);

        // Assert
        result.Should().Contain("Image Analysis Instructions");
    }

    [Fact]
    public void Build_WithTags_ListsTagNames()
    {
        // Arrange
        var habits = Array.Empty<Habit>();
        var facts = Array.Empty<UserFact>();
        var tags = new[]
        {
            Tag.Create(TestUserId, "Health", "#00ff00").Value,
            Tag.Create(TestUserId, "Fitness", "#ff0000").Value
        };

        // Act
        var result = SystemPromptBuilder.BuildSystemPrompt(
            habits, facts, userTags: tags);

        // Assert
        result.Should().Contain("Health");
        result.Should().Contain("Fitness");
    }

    [Fact]
    public void Build_IncludesTodayDate()
    {
        // Arrange
        var habits = Array.Empty<Habit>();
        var facts = Array.Empty<UserFact>();
        var today = new DateOnly(2026, 3, 20);

        // Act
        var result = SystemPromptBuilder.BuildSystemPrompt(
            habits, facts, userToday: today);

        // Assert
        result.Should().Contain("2026-03-20");
    }
}
