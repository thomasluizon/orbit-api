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
        IReadOnlyList<DayOfWeek>? days = null,
        bool isFlexible = false)
    {
        var result = Habit.Create(
            UserId,
            "Test Habit",
            unit,
            qty,
            dueDate: dueDate ?? Anchor,
            days: days,
            isFlexible: isFlexible);

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

    // --- Flexible: GetWindowStart ---

    [Fact]
    public void GetWindowStart_Weekly_ReturnsISOMonday()
    {
        // Arrange - Wednesday Jan 8, 2025
        var habit = CreateHabit(FrequencyUnit.Week, 3, isFlexible: true);
        var wednesday = new DateOnly(2025, 1, 8);

        // Act
        var windowStart = HabitScheduleService.GetWindowStart(habit, wednesday);

        // Assert - ISO Monday = Jan 6
        windowStart.Should().Be(new DateOnly(2025, 1, 6));
        windowStart.DayOfWeek.Should().Be(DayOfWeek.Monday);
    }

    [Fact]
    public void GetWindowStart_Weekly_MondayReturnsSameDay()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 3, isFlexible: true);
        var monday = new DateOnly(2025, 1, 6);

        var windowStart = HabitScheduleService.GetWindowStart(habit, monday);

        windowStart.Should().Be(monday);
    }

    [Fact]
    public void GetWindowStart_Weekly_SundayReturnsPreviousMonday()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 3, isFlexible: true);
        var sunday = new DateOnly(2025, 1, 12);

        var windowStart = HabitScheduleService.GetWindowStart(habit, sunday);

        windowStart.Should().Be(new DateOnly(2025, 1, 6));
    }

    [Fact]
    public void GetWindowStart_Monthly_ReturnsFirstOfMonth()
    {
        var habit = CreateHabit(FrequencyUnit.Month, 5, isFlexible: true);
        var midMonth = new DateOnly(2025, 3, 15);

        var windowStart = HabitScheduleService.GetWindowStart(habit, midMonth);

        windowStart.Should().Be(new DateOnly(2025, 3, 1));
    }

    [Fact]
    public void GetWindowStart_Yearly_ReturnsJan1()
    {
        var habit = CreateHabit(FrequencyUnit.Year, 10, isFlexible: true);
        var midYear = new DateOnly(2025, 7, 15);

        var windowStart = HabitScheduleService.GetWindowStart(habit, midYear);

        windowStart.Should().Be(new DateOnly(2025, 1, 1));
    }

    [Fact]
    public void GetWindowStart_Daily_ReturnsSameDay()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 2, isFlexible: true);
        var date = new DateOnly(2025, 3, 15);

        var windowStart = HabitScheduleService.GetWindowStart(habit, date);

        windowStart.Should().Be(date);
    }

    // --- Flexible: GetWindowEnd ---

    [Fact]
    public void GetWindowEnd_Weekly_ReturnsSunday()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 3, isFlexible: true);
        var wednesday = new DateOnly(2025, 1, 8);

        var windowEnd = HabitScheduleService.GetWindowEnd(habit, wednesday);

        windowEnd.Should().Be(new DateOnly(2025, 1, 12)); // Sunday
        windowEnd.DayOfWeek.Should().Be(DayOfWeek.Sunday);
    }

    [Fact]
    public void GetWindowEnd_Monthly_ReturnsLastOfMonth()
    {
        var habit = CreateHabit(FrequencyUnit.Month, 5, isFlexible: true);

        // February 2025 (non-leap)
        var feb = new DateOnly(2025, 2, 15);
        HabitScheduleService.GetWindowEnd(habit, feb).Should().Be(new DateOnly(2025, 2, 28));

        // February 2024 (leap)
        var febLeap = new DateOnly(2024, 2, 15);
        HabitScheduleService.GetWindowEnd(habit, febLeap).Should().Be(new DateOnly(2024, 2, 29));
    }

    [Fact]
    public void GetWindowEnd_Yearly_ReturnsDec31()
    {
        var habit = CreateHabit(FrequencyUnit.Year, 10, isFlexible: true);
        var midYear = new DateOnly(2025, 7, 15);

        var windowEnd = HabitScheduleService.GetWindowEnd(habit, midYear);

        windowEnd.Should().Be(new DateOnly(2025, 12, 31));
    }

    [Fact]
    public void GetWindowEnd_Daily_ReturnsSameDay()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 2, isFlexible: true);
        var date = new DateOnly(2025, 3, 15);

        var windowEnd = HabitScheduleService.GetWindowEnd(habit, date);

        windowEnd.Should().Be(date);
    }

    // --- Flexible: GetCompletedInWindow ---

    [Fact]
    public void GetCompletedInWindow_CountsLogsInCurrentWindow()
    {
        // Arrange - 3x per week flexible habit
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 3, dueDate: monday, isFlexible: true);

        // Log on Monday and Wednesday
        habit.Log(monday);
        habit.Log(new DateOnly(2025, 1, 8)); // Wednesday

        // Act - check from Thursday's perspective (same week)
        var completed = HabitScheduleService.GetCompletedInWindow(habit, new DateOnly(2025, 1, 9), habit.Logs);

        // Assert
        completed.Should().Be(2);
    }

    [Fact]
    public void GetCompletedInWindow_ExcludesLogsFromOtherWindows()
    {
        // Arrange - 3x per week flexible habit
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 3, dueDate: monday, isFlexible: true);

        // Log on Monday (week 1)
        habit.Log(monday);
        // Log on next Monday (week 2)
        habit.Log(new DateOnly(2025, 1, 13));

        // Act - check from next Wednesday (week 2)
        var completed = HabitScheduleService.GetCompletedInWindow(habit, new DateOnly(2025, 1, 15), habit.Logs);

        // Assert - only 1 log in week 2
        completed.Should().Be(1);
    }

    [Fact]
    public void GetCompletedInWindow_CountsMultipleLogsOnSameDay()
    {
        // Arrange - 5x per week flexible habit
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 5, dueDate: monday, isFlexible: true);

        // Log twice on Monday
        habit.Log(monday);
        habit.Log(monday);

        // Act
        var completed = HabitScheduleService.GetCompletedInWindow(habit, monday, habit.Logs);

        // Assert
        completed.Should().Be(2);
    }

    // --- Flexible: GetRemainingCompletions ---

    [Fact]
    public void GetRemainingCompletions_ReturnsCorrectRemaining()
    {
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 3, dueDate: monday, isFlexible: true);
        habit.Log(monday);

        var remaining = HabitScheduleService.GetRemainingCompletions(habit, monday, habit.Logs);

        remaining.Should().Be(2); // 3 target - 1 done = 2 remaining
    }

    [Fact]
    public void GetRemainingCompletions_NeverNegative()
    {
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 2, dueDate: monday, isFlexible: true);
        habit.Log(monday);
        habit.Log(monday);
        habit.Log(monday); // Over-complete

        var remaining = HabitScheduleService.GetRemainingCompletions(habit, monday, habit.Logs);

        remaining.Should().Be(0);
    }

    // --- Flexible: IsFlexibleHabitDueOnDate ---

    [Fact]
    public void IsFlexibleHabitDueOnDate_TargetNotReached_ReturnsTrue()
    {
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 3, dueDate: monday, isFlexible: true);
        habit.Log(monday);

        var result = HabitScheduleService.IsFlexibleHabitDueOnDate(habit, new DateOnly(2025, 1, 8), habit.Logs);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsFlexibleHabitDueOnDate_TargetReached_ReturnsFalse()
    {
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 2, dueDate: monday, isFlexible: true);
        habit.Log(monday);
        habit.Log(new DateOnly(2025, 1, 7));

        var result = HabitScheduleService.IsFlexibleHabitDueOnDate(habit, new DateOnly(2025, 1, 8), habit.Logs);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsFlexibleHabitDueOnDate_NonFlexibleHabit_ReturnsFalse()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 1);

        var result = HabitScheduleService.IsFlexibleHabitDueOnDate(habit, Anchor, habit.Logs);

        result.Should().BeFalse();
    }

    // --- Flexible: IsHabitDueOnDate delegates correctly ---

    [Fact]
    public void IsHabitDueOnDate_FlexibleHabit_TrueForAnyDateAfterAnchor()
    {
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 3, dueDate: monday, isFlexible: true);

        // Any date from anchor onwards returns true (flexible is "due" every day)
        HabitScheduleService.IsHabitDueOnDate(habit, monday).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, monday.AddDays(1)).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, monday.AddDays(30)).Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_FlexibleHabit_FalseBeforeAnchor()
    {
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 3, dueDate: monday, isFlexible: true);

        HabitScheduleService.IsHabitDueOnDate(habit, monday.AddDays(-1)).Should().BeFalse();
    }
}
