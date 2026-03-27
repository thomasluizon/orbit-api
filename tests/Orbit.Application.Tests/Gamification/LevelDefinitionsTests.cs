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
    [InlineData(50_000, 10, "Legend")]
    public void GetLevelForXp_ReturnsCorrectLevel(int xp, int expectedLevel, string expectedTitle)
    {
        var level = LevelDefinitions.GetLevelForXp(xp);

        level.Level.Should().Be(expectedLevel);
        level.Title.Should().Be(expectedTitle);
    }

    [Theory]
    [InlineData(0, 100)]       // Level 1 -> need 100 for level 2
    [InlineData(50, 50)]       // Level 1 -> need 50 more
    [InlineData(100, 200)]     // Level 2 -> need 200 more for level 3 (300)
    [InlineData(250, 50)]      // Level 2 -> need 50 more for level 3 (300)
    [InlineData(9999, 1)]      // Level 9 -> need 1 more for level 10 (10000)
    public void GetXpToNextLevel_ReturnsCorrectXpNeeded(int xp, int expectedXpToNext)
    {
        var xpToNext = LevelDefinitions.GetXpToNextLevel(xp);

        xpToNext.Should().Be(expectedXpToNext);
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(50_000)]
    public void GetXpToNextLevel_AtMaxLevel_ReturnsNull(int xp)
    {
        var xpToNext = LevelDefinitions.GetXpToNextLevel(xp);

        xpToNext.Should().BeNull();
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
}
