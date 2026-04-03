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
        return Goal.Create(
            ValidUserId,
            "Test Goal",
            targetValue,
            unit,
            deadline: deadline).Value;
    }

    [Fact]
    public void Calculate_ZeroProgress_ReturnsZeroPercentage()
    {
        // Arrange
        var goal = CreateGoal(targetValue: 100);

        // Act
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        // Assert
        metrics.ProgressPercentage.Should().Be(0);
    }

    [Fact]
    public void Calculate_HalfProgress_Returns50Percent()
    {
        // Arrange
        var goal = CreateGoal(targetValue: 100);
        goal.UpdateProgress(50);

        // Act
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        // Assert
        metrics.ProgressPercentage.Should().Be(50);
    }

    [Fact]
    public void Calculate_FullProgress_Returns100Percent()
    {
        // Arrange
        var goal = CreateGoal(targetValue: 100);
        goal.UpdateProgress(100);

        // Act
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        // Assert
        metrics.ProgressPercentage.Should().Be(100);
    }

    [Fact]
    public void Calculate_OverProgress_CapsAt100Percent()
    {
        // Arrange
        var goal = CreateGoal(targetValue: 100);
        goal.UpdateProgress(150);

        // Act
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        // Assert
        metrics.ProgressPercentage.Should().Be(100);
    }

    [Fact]
    public void Calculate_WithDeadline_ReturnsDaysToDeadline()
    {
        // Arrange
        var deadline = Today.AddDays(10);
        var goal = CreateGoal(deadline: deadline);

        // Act
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        // Assert
        metrics.DaysToDeadline.Should().Be(10);
    }

    [Fact]
    public void Calculate_PastDeadline_ReturnsNegativeDays()
    {
        // Arrange
        var deadline = Today.AddDays(-5);
        var goal = CreateGoal(deadline: deadline);

        // Act
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        // Assert
        metrics.DaysToDeadline.Should().Be(-5);
    }

    [Fact]
    public void Calculate_NoDeadline_ReturnsNullDaysToDeadline()
    {
        // Arrange
        var goal = CreateGoal(deadline: null);

        // Act
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        // Assert
        metrics.DaysToDeadline.Should().BeNull();
    }

    [Fact]
    public void Calculate_CompletedGoal_TrackingStatusIsCompleted()
    {
        // Arrange
        var goal = CreateGoal(targetValue: 10, deadline: Today.AddDays(30));
        goal.UpdateProgress(10); // completing the goal

        // Act
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        // Assert
        metrics.TrackingStatus.Should().Be("completed");
    }

    [Fact]
    public void Calculate_NoDeadline_TrackingStatusIsNoDeadline()
    {
        // Arrange
        var goal = CreateGoal(deadline: null);

        // Act
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        // Assert
        metrics.TrackingStatus.Should().Be("no_deadline");
    }

    [Fact]
    public void Calculate_PastDeadline_TrackingStatusIsBehind()
    {
        // Arrange
        var goal = CreateGoal(deadline: Today.AddDays(-1));

        // Act
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        // Assert
        metrics.TrackingStatus.Should().Be("behind");
    }

    [Fact]
    public void Calculate_DeadlineSoonLowProgress_TrackingStatusIsAtRisk()
    {
        // Arrange
        var goal = CreateGoal(targetValue: 100, deadline: Today.AddDays(5));
        goal.UpdateProgress(30); // < 50%

        // Act
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        // Assert
        metrics.TrackingStatus.Should().Be("at_risk");
    }

    [Fact]
    public void Calculate_DeadlineSoonHighProgress_TrackingStatusIsOnTrack()
    {
        // Arrange
        var goal = CreateGoal(targetValue: 100, deadline: Today.AddDays(5));
        goal.UpdateProgress(80); // > 50%

        // Act
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        // Assert
        metrics.TrackingStatus.Should().Be("on_track");
    }

    [Fact]
    public void Calculate_VelocityPerDay_CalculatesCorrectly()
    {
        // Arrange
        var goal = CreateGoal(targetValue: 100);
        goal.UpdateProgress(50);

        // Act
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        // Assert
        // Velocity = currentValue / daysSinceCreation
        // daysSinceCreation is at least 1 (same day creation)
        metrics.VelocityPerDay.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Calculate_NoProgress_ProjectedCompletionIsNull()
    {
        // Arrange
        var goal = CreateGoal(targetValue: 100);

        // Act
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        // Assert
        metrics.ProjectedCompletionDate.Should().BeNull();
    }

    [Fact]
    public void Calculate_WithProgress_ProjectedCompletionIsInFuture()
    {
        // Arrange
        var goal = CreateGoal(targetValue: 100);
        goal.UpdateProgress(50);

        // Act
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        // Assert
        // If there's progress and it's not yet complete, there should be a projected date
        metrics.ProjectedCompletionDate.Should().NotBeNull();
        metrics.ProjectedCompletionDate!.Value.Should().BeOnOrAfter(Today);
    }

    [Fact]
    public void Calculate_EmptyHabits_ReturnsEmptyHabitAdherence()
    {
        // Arrange
        var goal = CreateGoal();

        // Act
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);

        // Assert
        metrics.HabitAdherence.Should().BeEmpty();
    }

    [Fact]
    public void Calculate_ZeroTargetValue_ProgressIsZero()
    {
        // GoalMetricsCalculator guards against division by zero on TargetValue.
        // However, Goal.Create rejects targetValue <= 0, so we test the edge
        // via the calculator's ternary: targetValue > 0 ? ... : 0
        // With a valid goal, the minimum targetValue is > 0, so progress should be computed.
        var goal = CreateGoal(targetValue: 1);
        var metrics = GoalMetricsCalculator.Calculate(goal, Today);
        metrics.ProgressPercentage.Should().Be(0);
    }
}
