using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the CatchUpDueDate logic on the Habit entity that the
/// HabitDueDateAdvancementService relies on. The background service loop
/// and DB interactions are integration concerns.
/// </summary>
public class HabitDueDateAdvancementServiceTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static Habit CreateRecurringHabit(
        FrequencyUnit unit = FrequencyUnit.Day,
        int quantity = 1,
        DateOnly? dueDate = null,
        DateOnly? endDate = null)
    {
        return Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Test Habit",
            unit,
            quantity,
            DueDate: dueDate ?? Today.AddDays(-10),
            EndDate: endDate)).Value;
    }

    [Fact]
    public void CatchUpDueDate_DailyHabit_PastDue_AdvancesToToday()
    {
        // Arrange
        var habit = CreateRecurringHabit(FrequencyUnit.Day, 1, Today.AddDays(-5));

        // Act
        habit.CatchUpDueDate(Today);

        // Assert
        habit.DueDate.Should().BeOnOrAfter(Today);
    }

    [Fact]
    public void CatchUpDueDate_WeeklyHabit_PastDue_AdvancesByWeeks()
    {
        // Arrange
        var startDate = Today.AddDays(-21); // 3 weeks ago
        var habit = CreateRecurringHabit(FrequencyUnit.Week, 1, startDate);

        // Act
        habit.CatchUpDueDate(Today);

        // Assert
        habit.DueDate.Should().BeOnOrAfter(Today);
    }

    [Fact]
    public void CatchUpDueDate_MonthlyHabit_PastDue_AdvancesByMonths()
    {
        // Arrange
        var startDate = Today.AddMonths(-3);
        var habit = CreateRecurringHabit(FrequencyUnit.Month, 1, startDate);

        // Act
        habit.CatchUpDueDate(Today);

        // Assert
        habit.DueDate.Should().BeOnOrAfter(Today);
    }

    [Fact]
    public void CatchUpDueDate_FutureHabit_DoesNotChange()
    {
        // Arrange
        var futureDate = Today.AddDays(5);
        var habit = CreateRecurringHabit(FrequencyUnit.Day, 1, futureDate);

        // Act
        habit.CatchUpDueDate(Today);

        // Assert
        habit.DueDate.Should().Be(futureDate);
    }

    [Fact]
    public void CatchUpDueDate_TodayHabit_DoesNotChange()
    {
        // Arrange
        var habit = CreateRecurringHabit(FrequencyUnit.Day, 1, Today);

        // Act
        habit.CatchUpDueDate(Today);

        // Assert
        habit.DueDate.Should().Be(Today);
    }

    [Fact]
    public void CatchUpDueDate_EveryTwoDays_AdvancesCorrectly()
    {
        // Arrange
        var startDate = Today.AddDays(-7);
        var habit = CreateRecurringHabit(FrequencyUnit.Day, 2, startDate);

        // Act
        habit.CatchUpDueDate(Today);

        // Assert
        habit.DueDate.Should().BeOnOrAfter(Today);
        // Should be on an even step from the start date
        var daysDiff = habit.DueDate.DayNumber - startDate.DayNumber;
        (daysDiff % 2).Should().Be(0);
    }

    [Fact]
    public void CatchUpDueDate_WithEndDate_MarksCompletedWhenPastEnd()
    {
        // Arrange
        var startDate = Today.AddDays(-30);
        var endDate = Today.AddDays(-5);
        var habit = CreateRecurringHabit(FrequencyUnit.Day, 1, startDate, endDate);

        // Act
        habit.CatchUpDueDate(Today);

        // Assert
        habit.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void CatchUpDueDate_WithFutureEndDate_DoesNotMarkCompleted()
    {
        // Arrange
        var startDate = Today.AddDays(-5);
        var endDate = Today.AddDays(30);
        var habit = CreateRecurringHabit(FrequencyUnit.Day, 1, startDate, endDate);

        // Act
        habit.CatchUpDueDate(Today);

        // Assert
        habit.IsCompleted.Should().BeFalse();
        habit.DueDate.Should().BeOnOrAfter(Today);
    }

    [Fact]
    public void CatchUpDueDate_YearlyHabit_PastDue_AdvancesByYears()
    {
        // Arrange
        var startDate = Today.AddYears(-2);
        var habit = CreateRecurringHabit(FrequencyUnit.Year, 1, startDate);

        // Act
        habit.CatchUpDueDate(Today);

        // Assert
        habit.DueDate.Should().BeOnOrAfter(Today);
    }
}
