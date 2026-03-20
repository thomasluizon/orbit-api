using FluentAssertions;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Services;

public class HabitScheduleServiceTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Anchor = new(2025, 1, 6); // Monday

    private static Habit CreateHabit(
        FrequencyUnit? unit,
        int? qty,
        DateOnly? dueDate = null,
        IReadOnlyList<DayOfWeek>? days = null)
    {
        var result = Habit.Create(
            UserId,
            "Test Habit",
            unit,
            qty,
            dueDate: dueDate ?? Anchor,
            days: days);

        return result.Value;
    }

    // --- IsHabitDueOnDate ---

    [Fact]
    public void IsHabitDueOnDate_OneTime_ExactDate_True()
    {
        // Arrange
        var habit = CreateHabit(null, null, dueDate: new DateOnly(2025, 3, 15));

        // Act
        var result = HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 3, 15));

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_OneTime_DifferentDate_False()
    {
        // Arrange
        var habit = CreateHabit(null, null, dueDate: new DateOnly(2025, 3, 15));

        // Act
        var result = HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 3, 16));

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsHabitDueOnDate_DailyQty1_AnyDateAfterAnchor_True()
    {
        // Arrange
        var habit = CreateHabit(FrequencyUnit.Day, 1);

        // Act & Assert
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(1)).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(100)).Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_DailyQty2_EveryOtherDay()
    {
        // Arrange
        var habit = CreateHabit(FrequencyUnit.Day, 2);

        // Act & Assert
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(1)).Should().BeFalse();
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(2)).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(3)).Should().BeFalse();
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(4)).Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_DailyQty3_EveryThirdDay()
    {
        // Arrange
        var habit = CreateHabit(FrequencyUnit.Day, 3);

        // Act & Assert
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(1)).Should().BeFalse();
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(2)).Should().BeFalse();
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(3)).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(6)).Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_BeforeAnchor_False()
    {
        // Arrange
        var habit = CreateHabit(FrequencyUnit.Day, 1);

        // Act
        var result = HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(-1));

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsHabitDueOnDate_WeeklyQty1_SameWeekday_True()
    {
        // Arrange - Anchor is Monday 2025-01-06
        var habit = CreateHabit(FrequencyUnit.Week, 1);

        // Act - next Monday is 2025-01-13
        var result = HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 13));

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_WeeklyQty1_DifferentWeekday_False()
    {
        // Arrange - Anchor is Monday 2025-01-06
        var habit = CreateHabit(FrequencyUnit.Week, 1);

        // Act - Tuesday 2025-01-07
        var result = HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 7));

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsHabitDueOnDate_WeeklyQty2_EveryOtherWeek()
    {
        // Arrange - Anchor is Monday 2025-01-06
        var habit = CreateHabit(FrequencyUnit.Week, 2);

        // Act & Assert
        // Week 0 (anchor): due
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 6)).Should().BeTrue();
        // Week 1: not due
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 13)).Should().BeFalse();
        // Week 2: due
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 20)).Should().BeTrue();
        // Week 3: not due
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 27)).Should().BeFalse();
    }

    [Fact]
    public void IsHabitDueOnDate_MonthlyQty1_SameDay_True()
    {
        // Arrange - Anchor is 6th of month
        var habit = CreateHabit(FrequencyUnit.Month, 1);

        // Act - Feb 6th
        var result = HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 2, 6));

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_MonthlyQty1_DifferentDay_False()
    {
        // Arrange - Anchor is 6th of month
        var habit = CreateHabit(FrequencyUnit.Month, 1);

        // Act - Feb 7th
        var result = HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 2, 7));

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsHabitDueOnDate_Yearly_SameMonthAndDay_True()
    {
        // Arrange - Anchor is Jan 6
        var habit = CreateHabit(FrequencyUnit.Year, 1);

        // Act - Jan 6 next year
        var result = HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2026, 1, 6));

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_WithDaysFilter_MatchingDay_True()
    {
        // Arrange - Daily qty 1, only on Monday and Wednesday
        var habit = CreateHabit(
            FrequencyUnit.Day, 1,
            days: new[] { DayOfWeek.Monday, DayOfWeek.Wednesday });

        // Act - Wednesday 2025-01-08
        var result = HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 8));

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_WithDaysFilter_NonMatchingDay_False()
    {
        // Arrange - Daily qty 1, only on Monday and Wednesday
        var habit = CreateHabit(
            FrequencyUnit.Day, 1,
            days: new[] { DayOfWeek.Monday, DayOfWeek.Wednesday });

        // Act - Tuesday 2025-01-07
        var result = HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 7));

        // Assert
        result.Should().BeFalse();
    }

    // --- GetScheduledDates ---

    [Fact]
    public void GetScheduledDates_DailyOver7Days_Returns7Dates()
    {
        // Arrange
        var habit = CreateHabit(FrequencyUnit.Day, 1);
        var from = Anchor;
        var to = Anchor.AddDays(6);

        // Act
        var dates = HabitScheduleService.GetScheduledDates(habit, from, to);

        // Assert
        dates.Should().HaveCount(7);
        dates.First().Should().Be(from);
        dates.Last().Should().Be(to);
    }

    [Fact]
    public void GetScheduledDates_CapsAt366Days()
    {
        // Arrange
        var habit = CreateHabit(FrequencyUnit.Day, 1);
        var from = Anchor;
        var to = Anchor.AddDays(500); // Exceeds 366 cap

        // Act
        var dates = HabitScheduleService.GetScheduledDates(habit, from, to);

        // Assert - should be capped at 367 dates (from + 366 days)
        dates.Should().HaveCount(367);
        dates.Last().Should().Be(from.AddDays(366));
    }

    [Fact]
    public void GetScheduledDates_EmptyWhenFromAfterTo()
    {
        // Arrange
        var habit = CreateHabit(FrequencyUnit.Day, 1);
        var from = new DateOnly(2025, 3, 15);
        var to = new DateOnly(2025, 3, 10);

        // Act
        var dates = HabitScheduleService.GetScheduledDates(habit, from, to);

        // Assert
        dates.Should().BeEmpty();
    }
}
