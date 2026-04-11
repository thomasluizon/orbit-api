using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Services.Prompts;
using Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;
using Orbit.Infrastructure.Services.Prompts.Sections.Static;

namespace Orbit.Infrastructure.Tests.Services;

public class CoreIdentitySectionTests
{
    [Fact]
    public void Order_Is100()
    {
        new CoreIdentitySection().Order.Should().Be(100);
    }

    [Fact]
    public void ShouldInclude_AlwaysTrue()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        new CoreIdentitySection().ShouldInclude(ctx).Should().BeTrue();
    }

    [Fact]
    public void Build_ContainsOrbitIdentity()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new CoreIdentitySection().Build(ctx);

        result.Should().Contain("Orbit AI");
        result.Should().Contain("Habit Tracking Assistant");
    }
}

public class GlobalRulesSectionTests
{
    [Fact]
    public void Order_Is200()
    {
        new GlobalRulesSection().Order.Should().Be(200);
    }

    [Fact]
    public void ShouldInclude_AlwaysTrue()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        new GlobalRulesSection().ShouldInclude(ctx).Should().BeTrue();
    }

    [Fact]
    public void Build_ContainsCoreRules()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new GlobalRulesSection().Build(ctx);

        result.Should().Contain("Core Rules");
        result.Should().Contain("DUPLICATE PREVENTION");
        result.Should().Contain("ACTION-ORIENTED");
    }
}

public class TodayDateSectionTests
{
    [Fact]
    public void Order_Is650()
    {
        new TodayDateSection().Order.Should().Be(650);
    }

    [Fact]
    public void ShouldInclude_AlwaysTrue()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        new TodayDateSection().ShouldInclude(ctx).Should().BeTrue();
    }

    [Fact]
    public void Build_WithUserToday_UsesProvidedDate()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, new DateOnly(2026, 6, 15), null);
        var result = new TodayDateSection().Build(ctx);

        result.Should().Contain("2026-06-15");
    }

    [Fact]
    public void Build_WithoutUserToday_UsesUtcNow()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new TodayDateSection().Build(ctx);

        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        result.Should().Contain(utcToday);
    }
}

public class HabitCountSectionTests
{
    [Fact]
    public void Order_Is800()
    {
        new HabitCountSection().Order.Should().Be(800);
    }

    [Fact]
    public void Build_ShowsCorrectCount()
    {
        var habits = new List<Habit>
        {
            Habit.Create(new HabitCreateParams(Guid.NewGuid(), "H1", null, null, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value,
            Habit.Create(new HabitCreateParams(Guid.NewGuid(), "H2", null, null, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value,
        };
        var ctx = new PromptContext(habits, new List<UserFact>(), false, null, null, null, null);
        var result = new HabitCountSection().Build(ctx);

        result.Should().Contain("2 active habits");
    }

    [Fact]
    public void Build_EmptyHabits_ShowsZero()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        var result = new HabitCountSection().Build(ctx);

        result.Should().Contain("0 active habits");
    }
}

public class ImageInstructionsSectionTests
{
    [Fact]
    public void Order_Is700()
    {
        new ImageInstructionsSection().Order.Should().Be(700);
    }

    [Fact]
    public void ShouldInclude_HasImageTrue_ReturnsTrue()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), true, null, null, null, null);
        new ImageInstructionsSection().ShouldInclude(ctx).Should().BeTrue();
    }

    [Fact]
    public void ShouldInclude_HasImageFalse_ReturnsFalse()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), false, null, null, null, null);
        new ImageInstructionsSection().ShouldInclude(ctx).Should().BeFalse();
    }

    [Fact]
    public void Build_ContainsImageAnalysisInstructions()
    {
        var ctx = new PromptContext(new List<Habit>(), new List<UserFact>(), true, null, null, new DateOnly(2026, 4, 10), null);
        var result = new ImageInstructionsSection().Build(ctx);

        result.Should().Contain("Image Analysis Instructions");
        result.Should().Contain("Extract EVERYTHING visible");
    }
}
