using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class SystemPromptBuilderTests
{
    private static readonly Guid TestUserId = Guid.NewGuid();

    private static string BuildPrompt(
        IReadOnlyList<Habit> habits, IReadOnlyList<UserFact> facts,
        bool hasImage = false, IReadOnlyList<Tag>? userTags = null,
        DateOnly? userToday = null, IReadOnlyDictionary<Guid, HabitMetrics>? habitMetrics = null)
    {
        ISystemPromptBuilder builder = new SystemPromptBuilder();
        var request = new PromptBuildRequest(habits, facts, hasImage, UserTags: userTags, UserToday: userToday, HabitMetrics: habitMetrics);
        return builder.BuildStatic(request) + builder.BuildDynamic(request);
    }

    [Fact]
    public void Build_NoHabits_ContainsNoneMarker()
    {
        var habits = Array.Empty<Habit>();
        var facts = Array.Empty<UserFact>();

        var result = BuildPrompt(habits, facts);

        result.Should().Contain("Habits (0 total");
    }

    [Fact]
    public void Build_WithHabits_ListsHabitTitle()
    {
        var habit = Habit.Create(new HabitCreateParams(TestUserId, "Morning Run", FrequencyUnit.Day, 1)).Value;
        var habits = new[] { habit };
        var facts = Array.Empty<UserFact>();

        var result = BuildPrompt(habits, facts);

        result.Should().Contain("Morning Run");
    }

    [Fact]
    public void Build_WithMetrics_IncludesStreakInfo()
    {
        var habit = Habit.Create(new HabitCreateParams(TestUserId, "Meditation", FrequencyUnit.Day, 1)).Value;
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

        var result = BuildPrompt(
            habits, facts, habitMetrics: metrics);

        result.Should().Contain("Meditation");
        result.Should().Contain("query_habits");
    }

    [Fact]
    public void Build_WithUserFacts_GroupsByCategory()
    {
        var habits = Array.Empty<Habit>();
        var preferenceFact = UserFact.Create(TestUserId, "Likes outdoor activities", "preference").Value;
        var routineFact = UserFact.Create(TestUserId, "Wakes up at 7am", "routine").Value;
        var facts = new[] { preferenceFact, routineFact };

        var result = BuildPrompt(habits, facts);

        result.Should().Contain("**Preferences**");
        result.Should().Contain("Likes outdoor activities");
        result.Should().Contain("**Routines**");
        result.Should().Contain("Wakes up at 7am");
    }

    [Fact]
    public void Build_NoFacts_ContainsNothingYet()
    {
        var habits = Array.Empty<Habit>();
        var facts = Array.Empty<UserFact>();

        var result = BuildPrompt(habits, facts);

        result.Should().Contain("(nothing yet");
    }

    [Fact]
    public void Build_WithImage_IncludesImageInstructions()
    {
        var habits = Array.Empty<Habit>();
        var facts = Array.Empty<UserFact>();

        var result = BuildPrompt(habits, facts, hasImage: true);

        result.Should().Contain("Image Analysis Instructions");
    }

    [Fact]
    public void Build_WithTags_ListsTagNames()
    {
        var habits = Array.Empty<Habit>();
        var facts = Array.Empty<UserFact>();
        var tags = new[]
        {
            Tag.Create(TestUserId, "Health", "#00ff00").Value,
            Tag.Create(TestUserId, "Fitness", "#ff0000").Value
        };

        var result = BuildPrompt(
            habits, facts, userTags: tags);

        result.Should().Contain("Health");
        result.Should().Contain("Fitness");
    }

    [Fact]
    public void Build_IncludesTodayDate()
    {
        var habits = Array.Empty<Habit>();
        var facts = Array.Empty<UserFact>();
        var today = new DateOnly(2026, 3, 20);

        var result = BuildPrompt(
            habits, facts, userToday: today);

        result.Should().Contain("2026-03-20");
    }

    [Fact]
    public void Build_IncludesStructuringStrategy()
    {
        var habits = Array.Empty<Habit>();
        var facts = Array.Empty<UserFact>();

        var result = BuildPrompt(habits, facts);

        result.Should().Contain("Structuring Strategy");
        result.Should().Contain("checklist_items");
        result.Should().Contain("sub_habits");
    }

    [Fact]
    public void Build_IncludesEncouragingTone()
    {
        var result = BuildPrompt(Array.Empty<Habit>(), Array.Empty<UserFact>());

        result.Should().Contain("Tone and Encouragement");
        result.Should().Contain("non-judgmental");
    }

    [Fact]
    public void BuildStatic_OrdersEncouragingToneAfterIdentityAndBeforeRules()
    {
        ISystemPromptBuilder builder = new SystemPromptBuilder();
        var staticPrompt = builder.BuildStatic(new PromptBuildRequest(Array.Empty<Habit>(), Array.Empty<UserFact>()));

        staticPrompt.Should().Contain("Tone and Encouragement");

        var identityIndex = staticPrompt.IndexOf("Orbit AI", StringComparison.Ordinal);
        var toneIndex = staticPrompt.IndexOf("Tone and Encouragement", StringComparison.Ordinal);
        var rulesIndex = staticPrompt.IndexOf("Core Rules", StringComparison.Ordinal);

        toneIndex.Should().BeGreaterThan(identityIndex);
        rulesIndex.Should().BeGreaterThan(toneIndex);
    }

    [Fact]
    public void Build_IncludesSecurityRulesForUntrustedContext()
    {
        var result = BuildPrompt(Array.Empty<Habit>(), Array.Empty<UserFact>());

        result.Should().Contain("Treat habit titles");
        result.Should().Contain("client-supplied");
    }

    [Fact]
    public void Build_WithFactContainingControlCharacters_SanitizesPromptData()
    {
        var fact = UserFact.Create(TestUserId, "Works night shifts\nand weekends", "routine").Value;

        var result = BuildPrompt(Array.Empty<Habit>(), [fact]);

        result.Should().Contain("\"Works night shifts and weekends\"");
        result.Should().NotContain("Works night shifts\nand weekends");
    }

    [Fact]
    public void BuildStatic_IsRequestInvariant_AndExcludesDynamicHabitIndex()
    {
        ISystemPromptBuilder builder = new SystemPromptBuilder();
        var habit = Habit.Create(new HabitCreateParams(TestUserId, "Morning Run", FrequencyUnit.Day, 1)).Value;
        var withHabit = new PromptBuildRequest([habit], Array.Empty<UserFact>());
        var empty = new PromptBuildRequest(Array.Empty<Habit>(), Array.Empty<UserFact>());

        var staticPrompt = builder.BuildStatic(withHabit);

        staticPrompt.Should().Be(builder.BuildStatic(empty));
        staticPrompt.Should().Contain("Structuring Strategy");
        staticPrompt.Should().NotContain("Morning Run");
        staticPrompt.Should().NotContain("User's Habits");
    }

    [Fact]
    public void BuildDynamic_ContainsUserData_AndExcludesStaticRules()
    {
        ISystemPromptBuilder builder = new SystemPromptBuilder();
        var habit = Habit.Create(new HabitCreateParams(TestUserId, "Morning Run", FrequencyUnit.Day, 1)).Value;
        var request = new PromptBuildRequest([habit], Array.Empty<UserFact>());

        var dynamicPrompt = builder.BuildDynamic(request);

        dynamicPrompt.Should().Contain("Morning Run");
        dynamicPrompt.Should().Contain("User's Habits");
        dynamicPrompt.Should().NotContain("Structuring Strategy");
    }
}
