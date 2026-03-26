using FluentAssertions;
using Orbit.Application.Gamification;

namespace Orbit.Application.Tests.Gamification;

public class AchievementDefinitionsTests
{
    [Fact]
    public void All_Has25Achievements()
    {
        AchievementDefinitions.All.Should().HaveCount(25);
    }

    [Fact]
    public void All_HaveUniqueIds()
    {
        var ids = AchievementDefinitions.All.Select(a => a.Id).ToList();

        ids.Distinct().Should().HaveCount(ids.Count);
    }

    [Fact]
    public void All_HaveValidData()
    {
        AchievementDefinitions.All.Should().AllSatisfy(a =>
        {
            a.Id.Should().NotBeNullOrEmpty();
            a.Name.Should().NotBeNullOrEmpty();
            a.Description.Should().NotBeNullOrEmpty();
            a.XpReward.Should().BeGreaterThan(0);
            a.IconKey.Should().NotBeNullOrEmpty();
        });
    }

    [Fact]
    public void All_HavePositiveXpRewards()
    {
        AchievementDefinitions.All.Should().AllSatisfy(a =>
        {
            a.XpReward.Should().BeGreaterThanOrEqualTo(25);
        });
    }

    [Theory]
    [InlineData(AchievementDefinitions.FirstOrbit, "First Orbit")]
    [InlineData(AchievementDefinitions.Liftoff, "Liftoff")]
    [InlineData(AchievementDefinitions.WeekWarrior, "Week Warrior")]
    [InlineData(AchievementDefinitions.MonthlyMaster, "Monthly Master")]
    [InlineData(AchievementDefinitions.Centurion, "Centurion")]
    [InlineData(AchievementDefinitions.GettingMomentum, "Getting Momentum")]
    [InlineData(AchievementDefinitions.Dedicated, "Dedicated")]
    [InlineData(AchievementDefinitions.GoalCrusher, "Goal Crusher")]
    [InlineData(AchievementDefinitions.PerfectDay, "Perfect Day")]
    [InlineData(AchievementDefinitions.EarlyBird, "Early Bird")]
    [InlineData(AchievementDefinitions.Comeback, "Comeback")]
    [InlineData(AchievementDefinitions.BadHabitBreaker, "Bad Habit Breaker")]
    public void GetById_ValidId_ReturnsDefinition(string id, string expectedName)
    {
        var definition = AchievementDefinitions.GetById(id);

        definition.Should().NotBeNull();
        definition!.Name.Should().Be(expectedName);
    }

    [Fact]
    public void GetById_InvalidId_ReturnsNull()
    {
        var definition = AchievementDefinitions.GetById("nonexistent");

        definition.Should().BeNull();
    }

    [Fact]
    public void Categories_AllRepresented()
    {
        var categories = AchievementDefinitions.All.Select(a => a.Category).Distinct().ToList();

        // GettingStarted, Consistency, Volume, Goals, Perfection, Special
        categories.Should().HaveCount(6);
    }

    [Fact]
    public void Rarities_MultipleRepresented()
    {
        var rarities = AchievementDefinitions.All.Select(a => a.Rarity).Distinct().ToList();

        // Common, Uncommon, Rare, Epic, Legendary
        rarities.Should().HaveCount(5);
    }

    [Fact]
    public void GettingStartedCategory_Has3Achievements()
    {
        var gettingStarted = AchievementDefinitions.All
            .Where(a => a.Category == Domain.Enums.AchievementCategory.GettingStarted)
            .ToList();

        gettingStarted.Should().HaveCount(3);
    }

    [Fact]
    public void ConsistencyCategory_Has6Achievements()
    {
        var consistency = AchievementDefinitions.All
            .Where(a => a.Category == Domain.Enums.AchievementCategory.Consistency)
            .ToList();

        consistency.Should().HaveCount(6);
    }

    [Fact]
    public void VolumeCategory_Has5Achievements()
    {
        var volume = AchievementDefinitions.All
            .Where(a => a.Category == Domain.Enums.AchievementCategory.Volume)
            .ToList();

        volume.Should().HaveCount(5);
    }

    [Fact]
    public void GoalsCategory_Has4Achievements()
    {
        var goals = AchievementDefinitions.All
            .Where(a => a.Category == Domain.Enums.AchievementCategory.Goals)
            .ToList();

        goals.Should().HaveCount(4);
    }

    [Fact]
    public void ConstantKeys_MatchDefinitionIds()
    {
        // Verify key constants match their definition IDs
        AchievementDefinitions.GetById(AchievementDefinitions.FirstOrbit)!.Id
            .Should().Be("first_orbit");
        AchievementDefinitions.GetById(AchievementDefinitions.Liftoff)!.Id
            .Should().Be("liftoff");
        AchievementDefinitions.GetById(AchievementDefinitions.MissionControl)!.Id
            .Should().Be("mission_control");
        AchievementDefinitions.GetById(AchievementDefinitions.WeekWarrior)!.Id
            .Should().Be("week_warrior");
        AchievementDefinitions.GetById(AchievementDefinitions.LegendaryVolume)!.Id
            .Should().Be("legendary");
        AchievementDefinitions.GetById(AchievementDefinitions.BadHabitBreaker)!.Id
            .Should().Be("bad_habit_breaker");
    }
}
