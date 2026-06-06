using FluentAssertions;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Services;

public class GoalMetricsCalculatorTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static Goal CreateGoal(
        decimal targetValue = 100,
        string unit = "km",
        DateOnly? deadline = null)
    {
        return Goal.Create(new Goal.CreateGoalParams(
            ValidUserId,
            "Test Goal",
            targetValue,
            unit,
            Deadline: deadline)).Value;
    }

    [Fact]
    public void Calculate_ZeroProgress_ReturnsZeroPercentage()
    {
        var goal = CreateGoal(targetValue: 100);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        metrics.ProgressPercentage.Should().Be(0);
    }

    [Fact]
    public void Calculate_HalfProgress_Returns50Percent()
    {
        var goal = CreateGoal(targetValue: 100);
        goal.UpdateProgress(50);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        metrics.ProgressPercentage.Should().Be(50);
    }

    [Fact]
    public void Calculate_FullProgress_Returns100Percent()
    {
        var goal = CreateGoal(targetValue: 100);
        goal.UpdateProgress(100);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        metrics.ProgressPercentage.Should().Be(100);
    }

    [Fact]
    public void Calculate_OverProgress_CapsAt100Percent()
    {
        var goal = CreateGoal(targetValue: 100);
        goal.UpdateProgress(150);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        metrics.ProgressPercentage.Should().Be(100);
    }

    [Fact]
    public void Calculate_WithDeadline_ReturnsDaysToDeadline()
    {
        var deadline = Today.AddDays(10);
        var goal = CreateGoal(deadline: deadline);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        metrics.DaysToDeadline.Should().Be(10);
    }

    [Fact]
    public void Calculate_PastDeadline_ReturnsNegativeDays()
    {
        var deadline = Today.AddDays(-5);
        var goal = CreateGoal(deadline: deadline);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        metrics.DaysToDeadline.Should().Be(-5);
    }

    [Fact]
    public void Calculate_NoDeadline_ReturnsNullDaysToDeadline()
    {
        var goal = CreateGoal(deadline: null);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        metrics.DaysToDeadline.Should().BeNull();
    }

    [Fact]
    public void Calculate_CompletedGoal_TrackingStatusIsCompleted()
    {
        var goal = CreateGoal(targetValue: 10, deadline: Today.AddDays(30));
        goal.UpdateProgress(10);
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        metrics.TrackingStatus.Should().Be("completed");
    }

    [Fact]
    public void Calculate_NoDeadline_TrackingStatusIsNoDeadline()
    {
        var goal = CreateGoal(deadline: null);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        metrics.TrackingStatus.Should().Be("no_deadline");
    }

    [Fact]
    public void Calculate_PastDeadline_TrackingStatusIsBehind()
    {
        var goal = CreateGoal(deadline: Today.AddDays(-1));

        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        metrics.TrackingStatus.Should().Be("behind");
    }

    [Fact]
    public void Calculate_DeadlineSoonLowProgress_TrackingStatusIsAtRisk()
    {
        var goal = CreateGoal(targetValue: 100, deadline: Today.AddDays(5));
        goal.UpdateProgress(30);
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        metrics.TrackingStatus.Should().Be("at_risk");
    }

    [Fact]
    public void Calculate_DeadlineSoonHighProgress_TrackingStatusIsOnTrack()
    {
        var goal = CreateGoal(targetValue: 100, deadline: Today.AddDays(5));
        goal.UpdateProgress(80);
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        metrics.TrackingStatus.Should().Be("on_track");
    }

    [Fact]
    public void Calculate_VelocityPerDay_CalculatesCorrectly()
    {
        var goal = CreateGoal(targetValue: 100);
        goal.UpdateProgress(50);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        metrics.VelocityPerDay.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Calculate_NoProgress_ProjectedCompletionIsNull()
    {
        var goal = CreateGoal(targetValue: 100);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        metrics.ProjectedCompletionDate.Should().BeNull();
    }

    [Fact]
    public void Calculate_WithProgress_ProjectedCompletionIsInFuture()
    {
        var goal = CreateGoal(targetValue: 100);
        goal.UpdateProgress(50);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        metrics.ProjectedCompletionDate.Should().NotBeNull();
        metrics.ProjectedCompletionDate!.Value.Should().BeOnOrAfter(Today);
    }

    [Fact]
    public void Calculate_EmptyHabits_ReturnsEmptyHabitAdherence()
    {
        var goal = CreateGoal();

        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        metrics.HabitAdherence.Should().BeEmpty();
    }

    [Fact]
    public void Calculate_ZeroTargetValue_ProgressIsZero()
    {
        var goal = CreateGoal(targetValue: 1);
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);
        metrics.ProgressPercentage.Should().Be(0);
    }
}
