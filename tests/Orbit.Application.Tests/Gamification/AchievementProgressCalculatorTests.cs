using FluentAssertions;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Models;

namespace Orbit.Application.Tests.Gamification;

public class AchievementProgressCalculatorTests
{
    private static AchievementProgressMetrics Metrics(
        int currentStreak = 0, int totalCompletions = 0, int goalsCreated = 0, int goalsCompleted = 0,
        int friendsCount = 0, int cheersSent = 0, int earlyLogs = 0, int nightLogs = 0) =>
        new(currentStreak, totalCompletions, goalsCreated, goalsCompleted, friendsCount, cheersSent, earlyLogs, nightLogs);

    [Fact]
    public void Compute_LockedStreakAchievement_ReturnsCurrentAndTarget()
    {
        var weekWarrior = AchievementDefinitions.GetById(AchievementDefinitions.WeekWarrior)!;

        var (current, target) = AchievementProgressCalculator.Compute(weekWarrior, Metrics(currentStreak: 5), isEarned: false);

        current.Should().Be(5);
        target.Should().Be(7);
    }

    [Fact]
    public void Compute_ValueExceedsTarget_ClampsCurrentToTarget()
    {
        var dedicated = AchievementDefinitions.GetById(AchievementDefinitions.Dedicated)!;

        var (current, target) = AchievementProgressCalculator.Compute(dedicated, Metrics(totalCompletions: 120), isEarned: false);

        current.Should().Be(100);
        target.Should().Be(100);
    }

    [Theory]
    [InlineData(AchievementDefinitions.GoalSetter, 2, 2, 3)]
    [InlineData(AchievementDefinitions.Overachiever, 4, 4, 5)]
    [InlineData(AchievementDefinitions.SquadGoals, 3, 3, 5)]
    [InlineData(AchievementDefinitions.Cheerleader, 12, 12, 25)]
    [InlineData(AchievementDefinitions.EarlyBird, 6, 6, 10)]
    [InlineData(AchievementDefinitions.NightOwl, 8, 8, 10)]
    public void Compute_QuantifiableMetrics_MapToTheirValue(string id, int _, int expectedCurrent, int expectedTarget)
    {
        var definition = AchievementDefinitions.GetById(id)!;
        var metrics = Metrics(goalsCreated: 2, goalsCompleted: 4, friendsCount: 3, cheersSent: 12, earlyLogs: 6, nightLogs: 8);

        var (current, target) = AchievementProgressCalculator.Compute(definition, metrics, isEarned: false);

        current.Should().Be(expectedCurrent);
        target.Should().Be(expectedTarget);
    }

    [Fact]
    public void Compute_NoneMetricAchievement_ReturnsNullProgress()
    {
        var firstOrbit = AchievementDefinitions.GetById(AchievementDefinitions.FirstOrbit)!;

        var (current, target) = AchievementProgressCalculator.Compute(firstOrbit, Metrics(currentStreak: 99), isEarned: false);

        current.Should().BeNull();
        target.Should().BeNull();
    }

    [Fact]
    public void Compute_EarnedQuantifiableAchievement_ReportsFullBar()
    {
        var weekWarrior = AchievementDefinitions.GetById(AchievementDefinitions.WeekWarrior)!;

        var (current, target) = AchievementProgressCalculator.Compute(weekWarrior, Metrics(currentStreak: 0), isEarned: true);

        current.Should().Be(7);
        target.Should().Be(7);
    }

    [Fact]
    public void Compute_EarnedNoneMetricAchievement_StaysNull()
    {
        var perfectDay = AchievementDefinitions.GetById(AchievementDefinitions.PerfectDay)!;

        var (current, target) = AchievementProgressCalculator.Compute(perfectDay, Metrics(), isEarned: true);

        current.Should().BeNull();
        target.Should().BeNull();
    }
}
