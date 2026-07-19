using FluentAssertions;
using Orbit.Application.Gamification;

namespace Orbit.Application.Tests.Gamification;

public class LevelDefinitionsTests
{
    [Fact]
    public void All_Has10Levels()
    {
        LevelDefinitions.All.Should().HaveCount(10);
    }

    [Fact]
    public void All_LevelsAreOrdered()
    {
        var levels = LevelDefinitions.All;
        for (int i = 0; i < levels.Count - 1; i++)
        {
            levels[i].Level.Should().BeLessThan(levels[i + 1].Level);
            levels[i].XpRequired.Should().BeLessThan(levels[i + 1].XpRequired);
        }
    }

    [Theory]
    [InlineData(0, 1, "Starter")]
    [InlineData(50, 1, "Starter")]
    [InlineData(99, 1, "Starter")]
    [InlineData(100, 2, "Explorer")]
    [InlineData(299, 2, "Explorer")]
    [InlineData(300, 3, "Orbiter")]
    [InlineData(600, 4, "Navigator")]
    [InlineData(1000, 5, "Pilot")]
    [InlineData(1500, 6, "Captain")]
    [InlineData(2500, 7, "Commander")]
    [InlineData(4000, 8, "Admiral")]
    [InlineData(6000, 9, "Elite")]
    [InlineData(10_000, 10, "Legend")]
    [InlineData(12_099, 10, "Legend")]
    [InlineData(12_100, 11, "Legend")]
    [InlineData(40_000, 20, "Legend")]
    [InlineData(50_000, 22, "Legend")]
    public void GetLevelForXp_ReturnsCorrectLevel(int xp, int expectedLevel, string expectedTitle)
    {
        var level = LevelDefinitions.GetLevelForXp(xp);

        level.Level.Should().Be(expectedLevel);
        level.Title.Should().Be(expectedTitle);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(50, 50)]
    [InlineData(100, 200)]
    [InlineData(250, 50)]
    [InlineData(9999, 1)]
    [InlineData(10_000, 2_100)]
    [InlineData(12_100, 2_300)]
    [InlineData(50_000, 2_900)]
    public void GetXpToNextLevel_ReturnsCorrectXpNeeded(int xp, int expectedXpToNext)
    {
        var xpToNext = LevelDefinitions.GetXpToNextLevel(xp);

        xpToNext.Should().Be(expectedXpToNext);
    }

    [Fact]
    public void XpCurve_IsContinuousAtLevel10()
    {
        LevelDefinitions.XpRequiredForLevel(10).Should().Be(10_000);
        LevelDefinitions.All[9].XpRequired.Should().Be(10_000);
    }

    [Fact]
    public void XpCurve_SecondDifferenceIsConstant200_PastLevel10()
    {
        for (var level = 10; level <= 19; level++)
        {
            var increment = LevelDefinitions.XpRequiredForLevel(level + 1) - LevelDefinitions.XpRequiredForLevel(level);
            var nextIncrement = LevelDefinitions.XpRequiredForLevel(level + 2) - LevelDefinitions.XpRequiredForLevel(level + 1);

            (nextIncrement - increment).Should().Be(200);
        }
    }

    [Fact]
    public void GetLevelForXp_NegativeXp_ReturnsLevel1()
    {
        var level = LevelDefinitions.GetLevelForXp(-100);

        level.Level.Should().Be(1);
        level.Title.Should().Be("Starter");
    }

    [Fact]
    public void All_StartsAtLevel1()
    {
        LevelDefinitions.All[0].Level.Should().Be(1);
        LevelDefinitions.All[0].XpRequired.Should().Be(0);
    }

    [Theory]
    [InlineData(1, "starter")]
    [InlineData(2, "explorer")]
    [InlineData(10, "legend")]
    [InlineData(20, "legend")]
    public void TitleKeyForLevel_ReturnsLowercaseTitle(int level, string expectedKey)
    {
        LevelDefinitions.TitleKeyForLevel(level).Should().Be(expectedKey);
    }
}
