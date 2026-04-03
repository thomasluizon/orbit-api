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
        var result = Habit.Create(new HabitCreateParams(
            UserId,
            "Test Habit",
            unit,
            qty,
            DueDate: dueDate ?? Anchor,
            Days: days,
            IsFlexible: isFlexible));

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

    // --- EndDate tests ---

    [Fact]
    public void IsHabitDueOnDate_AfterEndDate_False()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Test", FrequencyUnit.Day, 1,
            DueDate: Anchor, EndDate: new DateOnly(2025, 1, 10))).Value;
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 11)).Should().BeFalse();
    }

    [Fact]
    public void IsHabitDueOnDate_OnEndDate_True()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Test", FrequencyUnit.Day, 1,
            DueDate: Anchor, EndDate: new DateOnly(2025, 1, 10))).Value;
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 10)).Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_BeforeEndDate_True()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Test", FrequencyUnit.Day, 1,
            DueDate: Anchor, EndDate: new DateOnly(2025, 1, 10))).Value;
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 8)).Should().BeTrue();
    }

    [Fact]
    public void GetScheduledDates_RespectsEndDate()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Test", FrequencyUnit.Day, 1,
            DueDate: Anchor, EndDate: new DateOnly(2025, 1, 8))).Value;
        var dates = HabitScheduleService.GetScheduledDates(habit, new DateOnly(2025, 1, 6), new DateOnly(2025, 1, 10));
        dates.Should().HaveCount(3);
        dates.Should().NotContain(new DateOnly(2025, 1, 9));
    }

    [Fact]
    public void IsHabitDueOnDate_NoEndDate_NotAffected()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 1);
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2030, 12, 31)).Should().BeTrue();
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

    // --- Monthly frequency edge cases ---

    [Fact]
    public void IsHabitDueOnDate_MonthlyQty2_EveryOtherMonth()
    {
        // Anchor is 6th of Jan
        var habit = CreateHabit(FrequencyUnit.Month, 2);

        // Feb (1 month) - not due
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 2, 6)).Should().BeFalse();
        // Mar (2 months) - due
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 3, 6)).Should().BeTrue();
        // Apr (3 months) - not due
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 4, 6)).Should().BeFalse();
        // May (4 months) - due
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 5, 6)).Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_MonthlyQty1_AnchorDay31_ShortMonth_ClampsToLastDay()
    {
        // Anchor on Jan 31
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test", FrequencyUnit.Month, 1, DueDate: new DateOnly(2025, 1, 31))).Value;

        // Feb has 28 days - should fire on Feb 28
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 2, 28)).Should().BeTrue();
        // Apr has 30 days - should fire on Apr 30
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 4, 30)).Should().BeTrue();
        // Mar has 31 days - should fire on Mar 31
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 3, 31)).Should().BeTrue();
    }

    // --- Yearly frequency edge cases ---

    [Fact]
    public void IsHabitDueOnDate_YearlyQty2_EveryOtherYear()
    {
        var habit = CreateHabit(FrequencyUnit.Year, 2);

        // 2026 (1 year) - not due
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2026, 1, 6)).Should().BeFalse();
        // 2027 (2 years) - due
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2027, 1, 6)).Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_Yearly_DifferentMonth_False()
    {
        var habit = CreateHabit(FrequencyUnit.Year, 1);

        // Same year, different month
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2026, 6, 6)).Should().BeFalse();
    }

    [Fact]
    public void IsHabitDueOnDate_Yearly_LeapDayAnchor_NonLeapYear_FallsBackToFeb28()
    {
        // Anchor on Feb 29 (leap year 2024)
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test", FrequencyUnit.Year, 1, DueDate: new DateOnly(2024, 2, 29))).Value;

        // 2025 is not a leap year - Feb 29 doesn't exist, should fire on Feb 28
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 2, 28)).Should().BeTrue();
        // 2026 also not leap
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2026, 2, 28)).Should().BeTrue();
        // 2028 is leap year - should fire on Feb 29
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2028, 2, 29)).Should().BeTrue();
    }

    // --- Day-of-week filtering with different frequencies ---

    [Fact]
    public void IsHabitDueOnDate_DailyWithDays_AllMatchingDaysAreTrue()
    {
        var habit = CreateHabit(
            FrequencyUnit.Day, 1,
            days: new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday });

        // Week of Jan 6 (Monday) to Jan 12 (Sunday)
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 6)).Should().BeTrue();   // Mon
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 7)).Should().BeFalse();  // Tue
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 8)).Should().BeTrue();   // Wed
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 9)).Should().BeFalse();  // Thu
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 10)).Should().BeTrue();  // Fri
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 11)).Should().BeFalse(); // Sat
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 12)).Should().BeFalse(); // Sun
    }

    // --- HasMissedPastOccurrence ---

    [Fact]
    public void HasMissedPastOccurrence_OneTimeTask_ReturnsFalse()
    {
        var habit = CreateHabit(null, null, dueDate: Anchor);

        HabitScheduleService.HasMissedPastOccurrence(habit, Anchor.AddDays(5)).Should().BeFalse();
    }

    [Fact]
    public void HasMissedPastOccurrence_BadHabit_ReturnsFalse()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Bad", FrequencyUnit.Day, 1, IsBadHabit: true, DueDate: Anchor)).Value;

        HabitScheduleService.HasMissedPastOccurrence(habit, Anchor.AddDays(5)).Should().BeFalse();
    }

    [Fact]
    public void HasMissedPastOccurrence_DailyHabitNoLogs_ReturnsTrue()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 1, dueDate: Anchor);
        var today = Anchor.AddDays(3);

        HabitScheduleService.HasMissedPastOccurrence(habit, today).Should().BeTrue();
    }

    [Fact]
    public void HasMissedPastOccurrence_DailyHabitAllLogged_ReturnsFalse()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 1, dueDate: Anchor);
        // Log all days from anchor to today-1
        habit.Log(Anchor, advanceDueDate: false);
        habit.Log(Anchor.AddDays(1), advanceDueDate: false);
        var today = Anchor.AddDays(2);

        HabitScheduleService.HasMissedPastOccurrence(habit, today).Should().BeFalse();
    }

    [Fact]
    public void HasMissedPastOccurrence_WeeklyHabit_LooksBackFarEnough()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 1, dueDate: Anchor);
        var today = Anchor.AddDays(14); // 2 weeks later

        HabitScheduleService.HasMissedPastOccurrence(habit, today).Should().BeTrue();
    }

    // --- GetScheduledDates with various frequencies ---

    [Fact]
    public void GetScheduledDates_WeeklyHabit_ReturnsCorrectDates()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 1, dueDate: Anchor);
        var from = Anchor;
        var to = Anchor.AddDays(28);

        var dates = HabitScheduleService.GetScheduledDates(habit, from, to);

        dates.Should().HaveCount(5); // Every Monday: Anchor + 7, +14, +21, +28
        dates.Should().AllSatisfy(d => d.DayOfWeek.Should().Be(DayOfWeek.Monday));
    }

    [Fact]
    public void GetScheduledDates_MonthlyHabit_ReturnsCorrectDates()
    {
        var habit = CreateHabit(FrequencyUnit.Month, 1, dueDate: Anchor);
        var from = Anchor;
        var to = Anchor.AddMonths(3);

        var dates = HabitScheduleService.GetScheduledDates(habit, from, to);

        dates.Should().HaveCount(4); // Jan 6, Feb 6, Mar 6, Apr 6
    }

    // --- GetLookbackDays ---

    [Theory]
    [InlineData(FrequencyUnit.Day, 3, 3)]
    [InlineData(FrequencyUnit.Week, 2, 14)]
    [InlineData(FrequencyUnit.Month, 1, 31)]
    [InlineData(FrequencyUnit.Year, 1, 366)]
    [InlineData(null, 1, 7)]
    public void GetLookbackDays_ReturnsCorrectValue(FrequencyUnit? unit, int qty, int expected)
    {
        HabitScheduleService.GetLookbackDays(unit, qty).Should().Be(expected);
    }

    // --- GetInstances ---

    [Fact]
    public void GetInstances_CompletedHabit_ReturnsEmpty()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test", null, null, DueDate: Anchor)).Value;
        habit.Log(Anchor); // completes one-time

        var instances = HabitScheduleService.GetInstances(habit, Anchor, Anchor.AddDays(7), Anchor);

        instances.Should().BeEmpty();
    }

    [Fact]
    public void GetInstances_FlexibleHabit_ReturnsEmpty()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 3, dueDate: Anchor, isFlexible: true);

        var instances = HabitScheduleService.GetInstances(habit, Anchor, Anchor.AddDays(7), Anchor);

        instances.Should().BeEmpty();
    }

    [Fact]
    public void GetInstances_OneTimeTask_InRange_ReturnsSingleInstance()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test", null, null, DueDate: Anchor)).Value;

        var instances = HabitScheduleService.GetInstances(
            habit, Anchor.AddDays(-1), Anchor.AddDays(1), Anchor.AddDays(1));

        instances.Should().HaveCount(1);
        instances[0].Date.Should().Be(Anchor);
    }

    [Fact]
    public void GetInstances_OneTimeTask_OutOfRange_ReturnsEmpty()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test", null, null, DueDate: Anchor)).Value;

        var instances = HabitScheduleService.GetInstances(
            habit, Anchor.AddDays(1), Anchor.AddDays(5), Anchor.AddDays(5));

        instances.Should().BeEmpty();
    }

    [Fact]
    public void GetInstances_RecurringHabit_OverdueDateMarkedOverdue()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 1, dueDate: Anchor);
        var today = Anchor.AddDays(3);

        var instances = HabitScheduleService.GetInstances(habit, Anchor, today, today);

        // Past unlogged dates should be overdue
        var overdue = instances.Where(i => i.Status == Orbit.Domain.Enums.InstanceStatus.Overdue);
        overdue.Should().NotBeEmpty();
    }

    [Fact]
    public void GetInstances_RecurringHabit_LoggedDateMarkedCompleted()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 1, dueDate: Anchor);
        habit.Log(Anchor, advanceDueDate: false);

        var instances = HabitScheduleService.GetInstances(habit, Anchor, Anchor, Anchor);

        instances.Should().HaveCount(1);
        instances[0].Status.Should().Be(Orbit.Domain.Enums.InstanceStatus.Completed);
    }

    // --- GetSkippedInWindow ---

    [Fact]
    public void GetSkippedInWindow_CountsOnlySkipLogs()
    {
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 3, dueDate: monday, isFlexible: true);

        habit.Log(monday);                    // completion log (Value=1)
        habit.SkipFlexible(monday.AddDays(1)); // skip log (Value=0)

        var skipped = HabitScheduleService.GetSkippedInWindow(habit, monday, habit.Logs);

        skipped.Should().Be(1);
    }

    // --- GetRemainingCompletions with skips ---

    [Fact]
    public void GetRemainingCompletions_SkipsReduceTarget()
    {
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 3, dueDate: monday, isFlexible: true);

        habit.SkipFlexible(monday); // 1 skip reduces target from 3 to 2

        var remaining = HabitScheduleService.GetRemainingCompletions(habit, monday, habit.Logs);

        remaining.Should().Be(2); // target 3 - 1 skip - 0 completions = 2
    }
}
