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

        var metrics = GoalMetricsCalculator.Calculate(goal, Today, 1);

        metrics.ProgressPercentage.Should().Be(0);
    }

    [Fact]
    public void Calculate_HalfProgress_Returns50Percent()
    {
        var goal = CreateGoal(targetValue: 100);
        goal.UpdateProgress(50);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today, 1);

        metrics.ProgressPercentage.Should().Be(50);
    }

    [Fact]
    public void Calculate_FullProgress_Returns100Percent()
    {
        var goal = CreateGoal(targetValue: 100);
        goal.UpdateProgress(100);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today, 1);

        metrics.ProgressPercentage.Should().Be(100);
    }

    [Fact]
    public void Calculate_OverProgress_CapsAt100Percent()
    {
        var goal = CreateGoal(targetValue: 100);
        goal.UpdateProgress(150);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today, 1);

        metrics.ProgressPercentage.Should().Be(100);
    }

    [Fact]
    public void Calculate_WithDeadline_ReturnsDaysToDeadline()
    {
        var deadline = Today.AddDays(10);
        var goal = CreateGoal(deadline: deadline);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today, 1);

        metrics.DaysToDeadline.Should().Be(10);
    }

    [Fact]
    public void Calculate_PastDeadline_ReturnsNegativeDays()
    {
        var deadline = Today.AddDays(-5);
        var goal = CreateGoal(deadline: deadline);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today, 1);

        metrics.DaysToDeadline.Should().Be(-5);
    }

    [Fact]
    public void Calculate_NoDeadline_ReturnsNullDaysToDeadline()
    {
        var goal = CreateGoal(deadline: null);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today, 1);

        metrics.DaysToDeadline.Should().BeNull();
    }

    [Fact]
    public void Calculate_CompletedGoal_TrackingStatusIsCompleted()
    {
        var goal = CreateGoal(targetValue: 10, deadline: Today.AddDays(30));
        goal.UpdateProgress(10);
        var metrics = GoalMetricsCalculator.Calculate(goal, Today, 1);

        metrics.TrackingStatus.Should().Be("completed");
    }

    [Fact]
    public void Calculate_NoDeadline_TrackingStatusIsNoDeadline()
    {
        var goal = CreateGoal(deadline: null);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today, 1);

        metrics.TrackingStatus.Should().Be("no_deadline");
    }

    [Fact]
    public void Calculate_PastDeadline_TrackingStatusIsBehind()
    {
        var goal = CreateGoal(deadline: Today.AddDays(-1));

        var metrics = GoalMetricsCalculator.Calculate(goal, Today, 1);

        metrics.TrackingStatus.Should().Be("behind");
    }

    [Fact]
    public void Calculate_DeadlineSoonLowProgress_TrackingStatusIsAtRisk()
    {
        var goal = CreateGoal(targetValue: 100, deadline: Today.AddDays(5));
        goal.UpdateProgress(30);
        var metrics = GoalMetricsCalculator.Calculate(goal, Today, 1);

        metrics.TrackingStatus.Should().Be("at_risk");
    }

    [Fact]
    public void Calculate_DeadlineSoonHighProgress_TrackingStatusIsOnTrack()
    {
        var goal = CreateGoal(targetValue: 100, deadline: Today.AddDays(5));
        goal.UpdateProgress(80);
        var metrics = GoalMetricsCalculator.Calculate(goal, Today, 1);

        metrics.TrackingStatus.Should().Be("on_track");
    }

    [Fact]
    public void Calculate_VelocityPerDay_CalculatesCorrectly()
    {
        var goal = CreateGoal(targetValue: 100);
        goal.UpdateProgress(50);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today, 1);

        metrics.VelocityPerDay.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Calculate_NoProgress_ProjectedCompletionIsNull()
    {
        var goal = CreateGoal(targetValue: 100);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today, 1);

        metrics.ProjectedCompletionDate.Should().BeNull();
    }

    [Fact]
    public void Calculate_WithProgress_ProjectedCompletionIsInFuture()
    {
        var goal = CreateGoal(targetValue: 100);
        goal.UpdateProgress(50);

        var metrics = GoalMetricsCalculator.Calculate(goal, Today, 1);

        metrics.ProjectedCompletionDate.Should().NotBeNull();
        metrics.ProjectedCompletionDate!.Value.Should().BeOnOrAfter(Today);
    }

    [Theory]
    [InlineData(10, 9)]
    [InlineData(10, 10)]
    [InlineData(10, 11)]
    [InlineData(0, 1)]
    public void Calculate_ForecastAtDateLimit_OnlyReturnsRepresentableDates(int availableDays, int daysToComplete)
    {
        var goal = CreateGoal(targetValue: 10_000_000);
        var userToday = DateOnly.MaxValue.AddDays(-availableDays);
        var elapsedDays = userToday.DayNumber - DateOnly.FromDateTime(goal.CreatedAtUtc).DayNumber;
        goal.Update(goal.Title, null, elapsedDays + daysToComplete, goal.Unit, null).IsSuccess.Should().BeTrue();
        goal.UpdateProgress(elapsedDays).IsSuccess.Should().BeTrue();

        var metrics = GoalMetricsCalculator.Calculate(goal, userToday, 1);

        metrics.VelocityPerDay.Should().Be(1);
        metrics.ProjectedCompletionDate.Should().Be(daysToComplete <= availableDays
            ? userToday.AddDays(daysToComplete)
            : null);
    }

    [Fact]
    public void Calculate_FractionalDuration_RoundsUpToNextDay()
    {
        var goal = CreateGoal(targetValue: 9);
        goal.UpdateProgress(2).IsSuccess.Should().BeTrue();
        var userToday = DateOnly.FromDateTime(goal.CreatedAtUtc);

        var metrics = GoalMetricsCalculator.Calculate(goal, userToday, 1);

        metrics.ProgressPercentage.Should().Be(22.2m);
        metrics.VelocityPerDay.Should().Be(2);
        metrics.ProjectedCompletionDate.Should().Be(userToday.AddDays(4));
    }

    [Theory]
    [InlineData(1_000_000_000)]
    [InlineData(3_000_000_000)]
    public void Calculate_DurationExceedsDateOrIntegerRange_ForecastIsUnavailable(long target)
    {
        var goal = CreateGoal(targetValue: target, deadline: Today.AddDays(3));
        goal.UpdateProgress(1).IsSuccess.Should().BeTrue();

        var metrics = GoalMetricsCalculator.Calculate(goal, Today, 1);

        metrics.ProjectedCompletionDate.Should().BeNull();
        metrics.ProgressPercentage.Should().Be(0);
        metrics.VelocityPerDay.Should().Be(1);
        metrics.DaysToDeadline.Should().Be(3);
        metrics.TrackingStatus.Should().Be("at_risk");
    }

    [Fact]
    public void Calculate_DurationExceedsDecimalRange_ForecastIsUnavailable()
    {
        var goal = CreateGoal(targetValue: decimal.MaxValue);
        goal.UpdateProgress(0.01m).IsSuccess.Should().BeTrue();
        var userToday = DateOnly.FromDateTime(goal.CreatedAtUtc);

        var metrics = GoalMetricsCalculator.Calculate(goal, userToday, 1);

        metrics.ProjectedCompletionDate.Should().BeNull();
        metrics.VelocityPerDay.Should().Be(0.01m);
        metrics.TrackingStatus.Should().Be("no_deadline");
    }

    [Fact]
    public void Calculate_EmptyHabits_ReturnsEmptyHabitAdherence()
    {
        var goal = CreateGoal();

        var metrics = GoalMetricsCalculator.Calculate(goal, Today, 1);

        metrics.HabitAdherence.Should().BeEmpty();
    }

    [Fact]
    public void Calculate_ZeroTargetValue_ProgressIsZero()
    {
        var goal = CreateGoal(targetValue: 1);
        var metrics = GoalMetricsCalculator.Calculate(goal, Today, 1);
        metrics.ProgressPercentage.Should().Be(0);
    }
}
