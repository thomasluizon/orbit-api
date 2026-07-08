using FluentAssertions;
using FsCheck.Xunit;
using Orbit.Application.Gamification;
using Orbit.Tests.Generators;

namespace Orbit.Application.Tests.Gamification;

[Properties(Arbitrary = new[] { typeof(OrbitArbitraries) }, MaxTest = 100, Replay = "(50000021,50000041)")]
public class LevelDefinitionsPropertyTests
{
    [Property]
    public void XpRequiredForLevel_IsStrictlyIncreasing(LadderLevel level)
    {
        var current = LevelDefinitions.XpRequiredForLevel(level.Value);
        var next = LevelDefinitions.XpRequiredForLevel(level.Value + 1);

        next.Should().BeGreaterThan(current);
    }

    [Property]
    public void GetLevelForXp_IsInverseOfXpRequiredForLevel(LadderXp xp)
    {
        var level = LevelDefinitions.GetLevelForXp(xp.Value).Level;

        LevelDefinitions.XpRequiredForLevel(level).Should().BeLessThanOrEqualTo(xp.Value);
        LevelDefinitions.XpRequiredForLevel(level + 1).Should().BeGreaterThan(xp.Value);
    }

    [Property]
    public void GetXpToNextLevel_IsAlwaysPositive(LadderXp xp)
    {
        LevelDefinitions.GetXpToNextLevel(xp.Value).Should().BePositive();
    }
}
