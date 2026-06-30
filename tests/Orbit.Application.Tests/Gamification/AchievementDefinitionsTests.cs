using FluentAssertions;
using Orbit.Application.Gamification;

namespace Orbit.Application.Tests.Gamification;

public class AchievementDefinitionsTests
{
    [Fact]
    public void All_Has39Achievements()
    {
        AchievementDefinitions.All.Should().HaveCount(39);
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

        categories.Should().HaveCount(9);
    }

    [Fact]
    public void Rarities_MultipleRepresented()
    {
        var rarities = AchievementDefinitions.All.Select(a => a.Rarity).Distinct().ToList();

        rarities.Should().HaveCount(5);
    }

    [Fact]
    public void GettingStartedCategory_Has4Achievements()
    {
        var gettingStarted = AchievementDefinitions.All
            .Where(a => a.Category == Domain.Enums.AchievementCategory.GettingStarted)
            .ToList();

        gettingStarted.Should().HaveCount(4);
    }

    [Fact]
    public void ConsistencyCategory_Has9Achievements()
    {
        var consistency = AchievementDefinitions.All
            .Where(a => a.Category == Domain.Enums.AchievementCategory.Consistency)
            .ToList();

        consistency.Should().HaveCount(9);
    }

    [Fact]
    public void SocialCategory_Has3Achievements()
    {
        var social = AchievementDefinitions.All
            .Where(a => a.Category == Domain.Enums.AchievementCategory.Social)
            .ToList();

        social.Should().HaveCount(3);
    }

    [Fact]
    public void SharingCategory_Has2Achievements()
    {
        var sharing = AchievementDefinitions.All
            .Where(a => a.Category == Domain.Enums.AchievementCategory.Sharing)
            .ToList();

        sharing.Should().HaveCount(2);
    }

    [Fact]
    public void TogetherCategory_Has3Achievements()
    {
        var together = AchievementDefinitions.All
            .Where(a => a.Category == Domain.Enums.AchievementCategory.Together)
            .ToList();

        together.Should().HaveCount(3);
    }

    [Theory]
    [InlineData(AchievementDefinitions.HalfYearHero, "Half-Year Hero", Domain.Enums.AchievementCategory.Consistency, Domain.Enums.AchievementRarity.Epic, 350)]
    [InlineData(AchievementDefinitions.StreakTitan, "Streak Titan", Domain.Enums.AchievementCategory.Consistency, Domain.Enums.AchievementRarity.Legendary, 750)]
    [InlineData(AchievementDefinitions.FirstCheer, "Good Vibes", Domain.Enums.AchievementCategory.Special, Domain.Enums.AchievementRarity.Common, 50)]
    [InlineData(AchievementDefinitions.OnboardingComplete, "All Systems Go", Domain.Enums.AchievementCategory.GettingStarted, Domain.Enums.AchievementRarity.Common, 50)]
    [InlineData(AchievementDefinitions.FirstFriend, "First Friend", Domain.Enums.AchievementCategory.Social, Domain.Enums.AchievementRarity.Common, 50)]
    [InlineData(AchievementDefinitions.SquadGoals, "Squad Goals", Domain.Enums.AchievementCategory.Social, Domain.Enums.AchievementRarity.Rare, 150)]
    [InlineData(AchievementDefinitions.Cheerleader, "Cheerleader", Domain.Enums.AchievementCategory.Social, Domain.Enums.AchievementRarity.Rare, 150)]
    [InlineData(AchievementDefinitions.ShowOff, "Show Off", Domain.Enums.AchievementCategory.Sharing, Domain.Enums.AchievementRarity.Uncommon, 75)]
    [InlineData(AchievementDefinitions.YearInReview, "Year in Review", Domain.Enums.AchievementCategory.Sharing, Domain.Enums.AchievementRarity.Uncommon, 75)]
    [InlineData(AchievementDefinitions.TeamPlayer, "Team Player", Domain.Enums.AchievementCategory.Together, Domain.Enums.AchievementRarity.Uncommon, 75)]
    [InlineData(AchievementDefinitions.MissionAccomplished, "Mission Accomplished", Domain.Enums.AchievementCategory.Together, Domain.Enums.AchievementRarity.Rare, 150)]
    [InlineData(AchievementDefinitions.BattleBuddy, "Battle Buddy", Domain.Enums.AchievementCategory.Together, Domain.Enums.AchievementRarity.Uncommon, 75)]
    [InlineData(AchievementDefinitions.StreakImmortal, "Streak Immortal", Domain.Enums.AchievementCategory.Consistency, Domain.Enums.AchievementRarity.Legendary, 1500)]
    [InlineData(AchievementDefinitions.Unstoppable, "Unstoppable", Domain.Enums.AchievementCategory.Volume, Domain.Enums.AchievementRarity.Legendary, 1000)]
    public void NewAchievements_HaveExpectedMetadata(
        string id,
        string expectedName,
        Domain.Enums.AchievementCategory expectedCategory,
        Domain.Enums.AchievementRarity expectedRarity,
        int expectedXp)
    {
        var definition = AchievementDefinitions.GetById(id);

        definition.Should().NotBeNull();
        definition!.Name.Should().Be(expectedName);
        definition.Category.Should().Be(expectedCategory);
        definition.Rarity.Should().Be(expectedRarity);
        definition.XpReward.Should().Be(expectedXp);
        definition.IconKey.Should().Be(id);
    }

    [Fact]
    public void VolumeCategory_Has6Achievements()
    {
        var volume = AchievementDefinitions.All
            .Where(a => a.Category == Domain.Enums.AchievementCategory.Volume)
            .ToList();

        volume.Should().HaveCount(6);
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
