using FluentAssertions;
using Orbit.Application.Challenges.Services;
using Orbit.Domain.Entities;

namespace Orbit.Application.Tests.Challenges;

public class ChallengeProgressCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 3, 20);
    private static readonly Guid HabitA = Guid.NewGuid();
    private static readonly Guid HabitB = Guid.NewGuid();

    private static HabitLog Log(Guid habitId, DateOnly date, decimal value) =>
        HabitLog.Create(habitId, date, value);

    [Fact]
    public void CoopGoal_CountsOnlyCompletionsInWindowAcrossContributingHabits()
    {
        var periodStart = Today.AddDays(-10);
        var logs = new[]
        {
            Log(HabitA, Today.AddDays(-1), 1),
            Log(HabitB, Today.AddDays(-2), 1),
            Log(HabitA, Today.AddDays(-3), 0),
            Log(HabitA, Today.AddDays(-30), 1),
            Log(Guid.NewGuid(), Today, 1),
        };

        var progress = ChallengeProgressCalculator.CalculateCoopGoalProgress(
            new[] { HabitA, HabitB }, logs, periodStart, Today);

        progress.Should().Be(2);
    }

    [Fact]
    public void CoopGoal_NoContributingHabits_ReturnsZero()
    {
        var progress = ChallengeProgressCalculator.CalculateCoopGoalProgress(
            [], [Log(HabitA, Today, 1)], Today.AddDays(-5), Today);

        progress.Should().Be(0);
    }

    [Fact]
    public void SharedStreak_AdvancesOnlyWhileEveryParticipantLogged()
    {
        var periodStart = Today.AddDays(-10);
        var logs = new List<HabitLog>
        {
            Log(HabitA, Today, 1), Log(HabitB, Today, 1),
            Log(HabitA, Today.AddDays(-1), 1), Log(HabitB, Today.AddDays(-1), 1),
            Log(HabitA, Today.AddDays(-2), 1), Log(HabitB, Today.AddDays(-2), 1),
            Log(HabitA, Today.AddDays(-3), 1),
        };

        var streak = ChallengeProgressCalculator.CalculateSharedStreak(
            [new[] { HabitA }, new[] { HabitB }], logs, periodStart, Today, Today);

        streak.Should().Be(3);
    }

    [Fact]
    public void SharedStreak_ResetsOnSingleParticipantMiss()
    {
        var periodStart = Today.AddDays(-10);
        var logs = new List<HabitLog>
        {
            Log(HabitA, Today, 1), Log(HabitB, Today, 1),
            Log(HabitA, Today.AddDays(-1), 1),
        };

        var streak = ChallengeProgressCalculator.CalculateSharedStreak(
            [new[] { HabitA }, new[] { HabitB }], logs, periodStart, Today, Today);

        streak.Should().Be(1);
    }

    [Fact]
    public void SharedStreak_UnfinishedToday_DoesNotBreakYesterdaysStreak()
    {
        var periodStart = Today.AddDays(-3);
        var logs = new List<HabitLog>
        {
            Log(HabitA, Today.AddDays(-1), 1), Log(HabitB, Today.AddDays(-1), 1),
            Log(HabitA, Today.AddDays(-2), 1), Log(HabitB, Today.AddDays(-2), 1),
        };

        var streak = ChallengeProgressCalculator.CalculateSharedStreak(
            [new[] { HabitA }, new[] { HabitB }], logs, periodStart, Today, Today);

        streak.Should().Be(2);
    }

    [Fact]
    public void SharedStreak_SkipValueDoesNotCountAsLogged()
    {
        var periodStart = Today.AddDays(-3);
        var logs = new List<HabitLog>
        {
            Log(HabitA, Today, 1), Log(HabitB, Today, 0),
        };

        var streak = ChallengeProgressCalculator.CalculateSharedStreak(
            [new[] { HabitA }, new[] { HabitB }], logs, periodStart, Today, Today);

        streak.Should().Be(0);
    }
}
