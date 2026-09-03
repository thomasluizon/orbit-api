using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Services;

public class HabitScheduleServiceTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Anchor = new(2025, 1, 6);
    private static Habit CreateHabit(
        FrequencyUnit? unit,
        int? qty,
        DateOnly? dueDate = null,
        IReadOnlyList<DayOfWeek>? days = null,
        bool isFlexible = false,
        int? intervalWeeks = null)
    {
        var result = Habit.Create(new HabitCreateParams(
            UserId,
            "Test Habit",
            unit,
            qty,
            DueDate: dueDate ?? Anchor,
            Days: days,
            IsFlexible: isFlexible,
            IntervalWeeks: intervalWeeks));

        return result.Value;
    }

    [Fact]
    public void IsHabitDueOnDate_OneTime_ExactDate_True()
    {
        var habit = CreateHabit(null, null, dueDate: new DateOnly(2025, 3, 15));

        var result = HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 3, 15));

        result.Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_OneTime_DifferentDate_False()
    {
        var habit = CreateHabit(null, null, dueDate: new DateOnly(2025, 3, 15));

        var result = HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 3, 16));

        result.Should().BeFalse();
    }

    [Fact]
    public void IsHabitDueOnDate_DailyQty1_AnyDateAfterAnchor_True()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 1);

        HabitScheduleService.IsHabitDueOnDate(habit, Anchor).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(1)).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(100)).Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_DailyQty2_EveryOtherDay()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 2);

        HabitScheduleService.IsHabitDueOnDate(habit, Anchor).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(1)).Should().BeFalse();
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(2)).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(3)).Should().BeFalse();
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(4)).Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_DailyQty3_EveryThirdDay()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 3);

        HabitScheduleService.IsHabitDueOnDate(habit, Anchor).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(1)).Should().BeFalse();
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(2)).Should().BeFalse();
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(3)).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(6)).Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_BeforeAnchor_False()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 1);

        var result = HabitScheduleService.IsHabitDueOnDate(habit, Anchor.AddDays(-1));

        result.Should().BeFalse();
    }

    [Fact]
    public void IsHabitDueOnDate_WeeklyQty1_SameWeekday_True()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 1);

        var result = HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 13));

        result.Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_WeeklyQty1_DifferentWeekday_False()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 1);

        var result = HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 7));

        result.Should().BeFalse();
    }

    [Fact]
    public void IsHabitDueOnDate_WeeklyQty2_EveryOtherWeek()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 2);

        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 6)).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 13)).Should().BeFalse();
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 20)).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 27)).Should().BeFalse();
    }

    [Fact]
    public void IsHabitDueOnDate_MonthlyQty1_SameDay_True()
    {
        var habit = CreateHabit(FrequencyUnit.Month, 1);

        var result = HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 2, 6));

        result.Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_MonthlyQty1_DifferentDay_False()
    {
        var habit = CreateHabit(FrequencyUnit.Month, 1);

        var result = HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 2, 7));

        result.Should().BeFalse();
    }

    [Fact]
    public void IsHabitDueOnDate_Yearly_SameMonthAndDay_True()
    {
        var habit = CreateHabit(FrequencyUnit.Year, 1);

        var result = HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2026, 1, 6));

        result.Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_WithDaysFilter_MatchingDay_True()
    {
        var habit = CreateHabit(
            FrequencyUnit.Day, 1,
            days: new[] { DayOfWeek.Monday, DayOfWeek.Wednesday });

        var result = HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 8));

        result.Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_WithDaysFilter_NonMatchingDay_False()
    {
        var habit = CreateHabit(
            FrequencyUnit.Day, 1,
            days: new[] { DayOfWeek.Monday, DayOfWeek.Wednesday });

        var result = HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 7));

        result.Should().BeFalse();
    }

    [Fact]
    public void GetScheduledDates_FixedThreeDayInterval_AlternatesWholeWeeks()
    {
        var anchor = new DateOnly(2025, 1, 7);
        var habit = CreateHabit(
            FrequencyUnit.Day,
            1,
            anchor,
            [DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday],
            intervalWeeks: 2);

        var dates = HabitScheduleService.GetScheduledDates(
            habit,
            anchor,
            anchor.AddDays(16),
            weekStartDay: 1);

        dates.Should().Equal(
            anchor,
            anchor.AddDays(1),
            anchor.AddDays(2),
            anchor.AddDays(14),
            anchor.AddDays(15),
            anchor.AddDays(16));
    }

    [Fact]
    public void IsHabitDueOnDate_IntervalParity_RespectsSundayAndMondayWeekStarts()
    {
        var sundayAnchor = new DateOnly(2025, 1, 5);
        var monday = sundayAnchor.AddDays(1);
        var habit = CreateHabit(
            FrequencyUnit.Day,
            1,
            sundayAnchor,
            [DayOfWeek.Sunday, DayOfWeek.Monday],
            intervalWeeks: 2);

        HabitScheduleService.IsHabitDueOnDate(habit, monday, weekStartDay: 0).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, monday, weekStartDay: 1).Should().BeFalse();
    }

    [Fact]
    public void FlexibleHabit_ThreeWeekInterval_IsInactiveForTwoWeeksAndKeepsCompletedStreak()
    {
        var anchor = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(
            FrequencyUnit.Week,
            3,
            anchor,
            isFlexible: true,
            intervalWeeks: 3);

        habit.Log(anchor, advanceDueDate: false).IsSuccess.Should().BeTrue();
        habit.Log(anchor.AddDays(1), advanceDueDate: false).IsSuccess.Should().BeTrue();
        habit.Log(anchor.AddDays(2), advanceDueDate: false).IsSuccess.Should().BeTrue();

        HabitScheduleService.IsFlexibleHabitDueOnDate(
            habit,
            anchor.AddDays(7),
            habit.Logs,
            weekStartDay: 1).Should().BeFalse();
        HabitScheduleService.IsFlexibleHabitDueOnDate(
            habit,
            anchor.AddDays(14),
            habit.Logs,
            weekStartDay: 1).Should().BeFalse();
        HabitScheduleService.IsFlexibleHabitDueOnDate(
            habit,
            anchor.AddDays(21),
            habit.Logs,
            weekStartDay: 1).Should().BeTrue();

        var metrics = HabitMetricsCalculator.Calculate(
            habit,
            anchor.AddDays(20),
            TimeZoneInfo.Utc,
            weekStartDay: 1);
        metrics.CurrentStreak.Should().Be(1);
    }

    [Fact]
    public void GetScheduledDates_NullInterval_MatchesLegacyFixturesAcrossNinetyDays()
    {
        var fixtures = new[]
        {
            CreateHabit(FrequencyUnit.Day, 1),
            CreateHabit(FrequencyUnit.Day, 2),
            CreateHabit(FrequencyUnit.Day, 1, days: [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday]),
            CreateHabit(FrequencyUnit.Week, 2),
            CreateHabit(FrequencyUnit.Month, 1),
            CreateHabit(FrequencyUnit.Year, 1),
            CreateHabit(FrequencyUnit.Week, 3, isFlexible: true)
        };

        foreach (var habit in fixtures)
        {
            habit.IntervalWeeks.Should().BeNull();
            var actual = HabitScheduleService.GetScheduledDates(
                habit,
                Anchor,
                Anchor.AddDays(89),
                weekStartDay: 1);
            var legacy = GetLegacyScheduledDates(habit, Anchor, Anchor.AddDays(89));

            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(actual)
                .Should().Equal(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(legacy));
        }
    }

    [Fact]
    public void GetScheduledDates_DailyOver7Days_Returns7Dates()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 1);
        var from = Anchor;
        var to = Anchor.AddDays(6);

        var dates = HabitScheduleService.GetScheduledDates(habit, from, to);

        dates.Should().HaveCount(7);
        dates.First().Should().Be(from);
        dates.Last().Should().Be(to);
    }

    [Fact]
    public void GetScheduledDates_CapsAt366Days()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 1);
        var from = Anchor;
        var to = Anchor.AddDays(500);
        var dates = HabitScheduleService.GetScheduledDates(habit, from, to);

        dates.Should().HaveCount(367);
        dates.Last().Should().Be(from.AddDays(366));
    }

    [Fact]
    public void GetScheduledDates_EmptyWhenFromAfterTo()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 1);
        var from = new DateOnly(2025, 3, 15);
        var to = new DateOnly(2025, 3, 10);

        var dates = HabitScheduleService.GetScheduledDates(habit, from, to);

        dates.Should().BeEmpty();
    }

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

    [Fact]
    public void GetWindowStart_Weekly_MondayStart_ReturnsMonday()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 3, isFlexible: true);
        var wednesday = new DateOnly(2025, 1, 8);

        var windowStart = HabitScheduleService.GetWindowStart(habit, wednesday, weekStartDay: 1);

        windowStart.Should().Be(new DateOnly(2025, 1, 6));
        windowStart.DayOfWeek.Should().Be(DayOfWeek.Monday);
    }

    [Fact]
    public void GetWindowStart_Weekly_SundayStart_ReturnsSunday()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 3, isFlexible: true);
        var wednesday = new DateOnly(2025, 1, 8);

        var windowStart = HabitScheduleService.GetWindowStart(habit, wednesday, weekStartDay: 0);

        windowStart.Should().Be(new DateOnly(2025, 1, 5));
        windowStart.DayOfWeek.Should().Be(DayOfWeek.Sunday);
    }

    [Fact]
    public void GetWindowStart_Weekly_MondayStart_MondayReturnsSameDay()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 3, isFlexible: true);
        var monday = new DateOnly(2025, 1, 6);

        var windowStart = HabitScheduleService.GetWindowStart(habit, monday, weekStartDay: 1);

        windowStart.Should().Be(monday);
    }

    [Fact]
    public void GetWindowStart_Weekly_SundayStart_SundayReturnsSameDay()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 3, isFlexible: true);
        var sunday = new DateOnly(2025, 1, 5);

        var windowStart = HabitScheduleService.GetWindowStart(habit, sunday, weekStartDay: 0);

        windowStart.Should().Be(sunday);
    }

    [Fact]
    public void GetWindowStart_Weekly_MondayStart_SundayReturnsPreviousMonday()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 3, isFlexible: true);
        var sunday = new DateOnly(2025, 1, 12);

        var windowStart = HabitScheduleService.GetWindowStart(habit, sunday, weekStartDay: 1);

        windowStart.Should().Be(new DateOnly(2025, 1, 6));
    }

    [Fact]
    public void GetWindowStart_Monthly_ReturnsFirstOfMonth()
    {
        var habit = CreateHabit(FrequencyUnit.Month, 5, isFlexible: true);
        var midMonth = new DateOnly(2025, 3, 15);

        var windowStart = HabitScheduleService.GetWindowStart(habit, midMonth, weekStartDay: 1);

        windowStart.Should().Be(new DateOnly(2025, 3, 1));
    }

    [Fact]
    public void GetWindowStart_Yearly_ReturnsJan1()
    {
        var habit = CreateHabit(FrequencyUnit.Year, 10, isFlexible: true);
        var midYear = new DateOnly(2025, 7, 15);

        var windowStart = HabitScheduleService.GetWindowStart(habit, midYear, weekStartDay: 1);

        windowStart.Should().Be(new DateOnly(2025, 1, 1));
    }

    [Fact]
    public void GetWindowStart_Daily_ReturnsSameDay()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 2, isFlexible: true);
        var date = new DateOnly(2025, 3, 15);

        var windowStart = HabitScheduleService.GetWindowStart(habit, date, weekStartDay: 1);

        windowStart.Should().Be(date);
    }

    [Fact]
    public void GetWindowEnd_Weekly_MondayStart_ReturnsSunday()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 3, isFlexible: true);
        var wednesday = new DateOnly(2025, 1, 8);

        var windowEnd = HabitScheduleService.GetWindowEnd(habit, wednesday, weekStartDay: 1);

        windowEnd.Should().Be(new DateOnly(2025, 1, 12));
        windowEnd.DayOfWeek.Should().Be(DayOfWeek.Sunday);
    }

    [Fact]
    public void GetWindowEnd_Weekly_SundayStart_ReturnsSaturday()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 3, isFlexible: true);
        var wednesday = new DateOnly(2025, 1, 8);

        var windowEnd = HabitScheduleService.GetWindowEnd(habit, wednesday, weekStartDay: 0);

        windowEnd.Should().Be(new DateOnly(2025, 1, 11));
        windowEnd.DayOfWeek.Should().Be(DayOfWeek.Saturday);
    }

    [Fact]
    public void GetWindowEnd_Monthly_ReturnsLastOfMonth()
    {
        var habit = CreateHabit(FrequencyUnit.Month, 5, isFlexible: true);

        var feb = new DateOnly(2025, 2, 15);
        HabitScheduleService.GetWindowEnd(habit, feb, weekStartDay: 1).Should().Be(new DateOnly(2025, 2, 28));

        var febLeap = new DateOnly(2024, 2, 15);
        HabitScheduleService.GetWindowEnd(habit, febLeap, weekStartDay: 1).Should().Be(new DateOnly(2024, 2, 29));
    }

    [Fact]
    public void GetWindowEnd_Yearly_ReturnsDec31()
    {
        var habit = CreateHabit(FrequencyUnit.Year, 10, isFlexible: true);
        var midYear = new DateOnly(2025, 7, 15);

        var windowEnd = HabitScheduleService.GetWindowEnd(habit, midYear, weekStartDay: 1);

        windowEnd.Should().Be(new DateOnly(2025, 12, 31));
    }

    [Fact]
    public void GetWindowEnd_Daily_ReturnsSameDay()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 2, isFlexible: true);
        var date = new DateOnly(2025, 3, 15);

        var windowEnd = HabitScheduleService.GetWindowEnd(habit, date, weekStartDay: 1);

        windowEnd.Should().Be(date);
    }

    [Fact]
    public void GetCompletedInWindow_CountsLogsInCurrentWindow()
    {
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 3, dueDate: monday, isFlexible: true);

        habit.Log(monday);
        habit.Log(new DateOnly(2025, 1, 8));
        var completed = HabitScheduleService.GetCompletedInWindow(habit, new DateOnly(2025, 1, 9), habit.Logs, weekStartDay: 1);

        completed.Should().Be(2);
    }

    [Fact]
    public void GetCompletedInWindow_SundayStart_SplitsLogsAcrossWeekBoundary()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 3, dueDate: new DateOnly(2025, 1, 5), isFlexible: true);

        habit.Log(new DateOnly(2025, 1, 11));        habit.Log(new DateOnly(2025, 1, 12));
        var sundayWeek = HabitScheduleService.GetCompletedInWindow(habit, new DateOnly(2025, 1, 11), habit.Logs, weekStartDay: 0);
        var mondayWeek = HabitScheduleService.GetCompletedInWindow(habit, new DateOnly(2025, 1, 11), habit.Logs, weekStartDay: 1);

        sundayWeek.Should().Be(1);
        mondayWeek.Should().Be(2);
    }

    [Fact]
    public void GetCompletedInWindow_ExcludesLogsFromOtherWindows()
    {
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 3, dueDate: monday, isFlexible: true);

        habit.Log(monday);
        habit.Log(new DateOnly(2025, 1, 13));

        var completed = HabitScheduleService.GetCompletedInWindow(habit, new DateOnly(2025, 1, 15), habit.Logs, weekStartDay: 1);

        completed.Should().Be(1);
    }

    [Fact]
    public void GetCompletedInWindow_CountsMultipleLogsOnSameDay()
    {
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 5, dueDate: monday, isFlexible: true);

        habit.Log(monday);
        habit.Log(monday);

        var completed = HabitScheduleService.GetCompletedInWindow(habit, monday, habit.Logs, weekStartDay: 1);

        completed.Should().Be(2);
    }

    [Fact]
    public void GetRemainingCompletions_ReturnsCorrectRemaining()
    {
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 3, dueDate: monday, isFlexible: true);
        habit.Log(monday);

        var remaining = HabitScheduleService.GetRemainingCompletions(habit, monday, habit.Logs, weekStartDay: 1);

        remaining.Should().Be(2);    }

    [Fact]
    public void GetRemainingCompletions_NeverNegative()
    {
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 2, dueDate: monday, isFlexible: true);
        habit.Log(monday);
        habit.Log(monday);
        habit.Log(monday);
        var remaining = HabitScheduleService.GetRemainingCompletions(habit, monday, habit.Logs, weekStartDay: 1);

        remaining.Should().Be(0);
    }

    [Fact]
    public void IsFlexibleHabitDueOnDate_TargetNotReached_ReturnsTrue()
    {
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 3, dueDate: monday, isFlexible: true);
        habit.Log(monday);

        var result = HabitScheduleService.IsFlexibleHabitDueOnDate(habit, new DateOnly(2025, 1, 8), habit.Logs, weekStartDay: 1);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsFlexibleHabitDueOnDate_TargetReached_ReturnsFalse()
    {
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 2, dueDate: monday, isFlexible: true);
        habit.Log(monday);
        habit.Log(new DateOnly(2025, 1, 7));

        var result = HabitScheduleService.IsFlexibleHabitDueOnDate(habit, new DateOnly(2025, 1, 8), habit.Logs, weekStartDay: 1);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsFlexibleHabitDueOnDate_SundayStart_NewWeekRequiresCompletionsAgain()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 1, dueDate: new DateOnly(2025, 1, 5), isFlexible: true);
        habit.Log(new DateOnly(2025, 1, 11));
        var sunday = new DateOnly(2025, 1, 12);

        var sundayStart = HabitScheduleService.IsFlexibleHabitDueOnDate(habit, sunday, habit.Logs, weekStartDay: 0);
        var mondayStart = HabitScheduleService.IsFlexibleHabitDueOnDate(habit, sunday, habit.Logs, weekStartDay: 1);

        sundayStart.Should().BeTrue();
        mondayStart.Should().BeFalse();
    }

    [Fact]
    public void IsFlexibleHabitDueOnDate_NonFlexibleHabit_ReturnsFalse()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 1);

        var result = HabitScheduleService.IsFlexibleHabitDueOnDate(habit, Anchor, habit.Logs, weekStartDay: 1);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsHabitDueOnDate_FlexibleHabit_TrueForAnyDateAfterAnchor()
    {
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 3, dueDate: monday, isFlexible: true);

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

    [Fact]
    public void IsHabitDueOnDate_MonthlyQty2_EveryOtherMonth()
    {
        var habit = CreateHabit(FrequencyUnit.Month, 2);

        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 2, 6)).Should().BeFalse();
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 3, 6)).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 4, 6)).Should().BeFalse();
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 5, 6)).Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_MonthlyQty1_AnchorDay31_ShortMonth_ClampsToLastDay()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test", FrequencyUnit.Month, 1, DueDate: new DateOnly(2025, 1, 31))).Value;

        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 2, 28)).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 4, 30)).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 3, 31)).Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_YearlyQty2_EveryOtherYear()
    {
        var habit = CreateHabit(FrequencyUnit.Year, 2);

        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2026, 1, 6)).Should().BeFalse();
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2027, 1, 6)).Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_Yearly_DifferentMonth_False()
    {
        var habit = CreateHabit(FrequencyUnit.Year, 1);

        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2026, 6, 6)).Should().BeFalse();
    }

    [Fact]
    public void IsHabitDueOnDate_Yearly_LeapDayAnchor_NonLeapYear_FallsBackToFeb28()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test", FrequencyUnit.Year, 1, DueDate: new DateOnly(2024, 2, 29))).Value;

        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 2, 28)).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2026, 2, 28)).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2028, 2, 29)).Should().BeTrue();
    }

    [Fact]
    public void IsHabitDueOnDate_DailyWithDays_AllMatchingDaysAreTrue()
    {
        var habit = CreateHabit(
            FrequencyUnit.Day, 1,
            days: new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday });

        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 6)).Should().BeTrue();        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 7)).Should().BeFalse();        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 8)).Should().BeTrue();        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 9)).Should().BeFalse();        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 10)).Should().BeTrue();        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 11)).Should().BeFalse();        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2025, 1, 12)).Should().BeFalse();    }

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
        habit.Log(Anchor);
        habit.Log(Anchor.AddDays(1));
        var today = Anchor.AddDays(2);

        HabitScheduleService.HasMissedPastOccurrence(habit, today).Should().BeFalse();
    }

    [Fact]
    public void HasMissedPastOccurrence_DueToday_ReturnsFalse()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 1, dueDate: Anchor);

        HabitScheduleService.HasMissedPastOccurrence(habit, Anchor).Should().BeFalse();
    }

    [Fact]
    public void HasMissedPastOccurrence_DueInFuture_ReturnsFalse()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 1, dueDate: Anchor.AddDays(7));

        HabitScheduleService.HasMissedPastOccurrence(habit, Anchor).Should().BeFalse();
    }

    [Fact]
    public void HasMissedPastOccurrence_FlexibleHabit_ReturnsFalse()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Flex", FrequencyUnit.Week, 3, IsFlexible: true, DueDate: Anchor)).Value;

        HabitScheduleService.HasMissedPastOccurrence(habit, Anchor.AddDays(10)).Should().BeFalse();
    }

    [Fact]
    public void HasMissedPastOccurrence_WeeklyHabit_LooksBackFarEnough()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 1, dueDate: Anchor);
        var today = Anchor.AddDays(14);
        HabitScheduleService.HasMissedPastOccurrence(habit, today).Should().BeTrue();
    }

    [Fact]
    public void GetScheduledDates_WeeklyHabit_ReturnsCorrectDates()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 1, dueDate: Anchor);
        var from = Anchor;
        var to = Anchor.AddDays(28);

        var dates = HabitScheduleService.GetScheduledDates(habit, from, to);

        dates.Should().HaveCount(5);        dates.Should().AllSatisfy(d => d.DayOfWeek.Should().Be(DayOfWeek.Monday));
    }

    [Fact]
    public void GetScheduledDates_MonthlyHabit_ReturnsCorrectDates()
    {
        var habit = CreateHabit(FrequencyUnit.Month, 1, dueDate: Anchor);
        var from = Anchor;
        var to = Anchor.AddMonths(3);

        var dates = HabitScheduleService.GetScheduledDates(habit, from, to);

        dates.Should().HaveCount(4);    }

    [Fact]
    public void GetHistoricalScheduledDates_DueDateAdvancedPastWindow_UsesScheduledStart()
    {
        var monthStart = new DateOnly(2026, 6, 1);
        var habit = CreateHabit(FrequencyUnit.Day, 1, dueDate: monthStart);
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!.SetValue(
            habit,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        habit.AdvanceDueDate(new DateOnly(2026, 9, 30));

        var dates = HabitScheduleService.GetHistoricalScheduledDates(
            habit,
            monthStart,
            new DateOnly(2026, 6, 30),
            TimeZoneInfo.Utc);

        dates.Should().HaveCount(30);
        dates.Should().StartWith(monthStart);
        dates.Should().EndWith(new DateOnly(2026, 6, 30));
    }

    [Fact]
    public void GetHistoricalScheduledDates_HabitCreatedAfterWindow_ReturnsEmpty()
    {
        var habit = CreateHabit(
            FrequencyUnit.Day,
            1,
            dueDate: new DateOnly(2026, 7, 1));
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!.SetValue(
            habit,
            new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc));

        var dates = HabitScheduleService.GetHistoricalScheduledDates(
            habit,
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 30),
            TimeZoneInfo.Utc);

        dates.Should().BeEmpty();
    }

    [Fact]
    public void GetHistoricalScheduledDates_PostponedOneTimeTask_UsesCurrentDueDate()
    {
        var originalDate = new DateOnly(2026, 6, 5);
        var postponedDate = new DateOnly(2026, 6, 20);
        var habit = CreateHabit(null, null, dueDate: originalDate);
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!.SetValue(
            habit,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        habit.PostponeTo(postponedDate);

        var dates = HabitScheduleService.GetHistoricalScheduledDates(
            habit,
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 30),
            TimeZoneInfo.Utc);

        dates.Should().Equal(postponedDate);
    }

    [Fact]
    public void GetHistoricalScheduledDates_DeletedDuringWindow_StopsOnLocalizedDeletionDate()
    {
        var monthStart = new DateOnly(2026, 6, 1);
        var habit = CreateHabit(FrequencyUnit.Day, 1, dueDate: monthStart);
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!.SetValue(
            habit,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        habit.SoftDelete(new DateTime(2026, 6, 15, 2, 30, 0, DateTimeKind.Utc));
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

        var dates = HabitScheduleService.GetHistoricalScheduledDates(
            habit,
            monthStart,
            new DateOnly(2026, 6, 30),
            timeZone);

        dates.Should().HaveCount(14);
        dates.Should().EndWith(new DateOnly(2026, 6, 14));
    }

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

    [Fact]
    public void GetInstances_CompletedHabit_ReturnsEmpty()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test", null, null, DueDate: Anchor)).Value;
        habit.Log(Anchor);
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

    [Fact]
    public void GetInstances_RecurringHabit_LoggedDateStillReturnedAfterDueDateAdvances()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 1, dueDate: Anchor);
        habit.Log(Anchor);

        var instances = HabitScheduleService.GetInstances(habit, Anchor, Anchor, Anchor);

        instances.Should().ContainSingle();
        instances[0].Date.Should().Be(Anchor);
        instances[0].Status.Should().Be(Orbit.Domain.Enums.InstanceStatus.Completed);
    }

    [Fact]
    public void GetInstances_RangeExceedingHorizon_CapsForwardWindow()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 1, dueDate: Anchor);
        var dateTo = Anchor.AddDays(200);

        var instances = HabitScheduleService.GetInstances(habit, Anchor, dateTo, Anchor);

        var horizonEnd = Anchor.AddDays(Orbit.Application.Common.AppConstants.MaxInstanceHorizonDays);
        instances.Should().HaveCount(Orbit.Application.Common.AppConstants.MaxInstanceHorizonDays + 1);
        instances.Should().OnlyContain(i => i.Date <= horizonEnd);
        instances[^1].Date.Should().Be(horizonEnd);
    }

    [Fact]
    public void GetInstances_RangeWithinHorizon_ReturnsEveryScheduledDate()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 1, dueDate: Anchor);
        var dateTo = Anchor.AddDays(30);

        var instances = HabitScheduleService.GetInstances(habit, Anchor, dateTo, Anchor);

        instances.Should().HaveCount(31);
        instances[^1].Date.Should().Be(dateTo);
    }

    [Fact]
    public void GetSkippedInWindow_CountsOnlySkipLogs()
    {
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 3, dueDate: monday, isFlexible: true);

        habit.Log(monday);        habit.SkipFlexible(monday.AddDays(1));
        var skipped = HabitScheduleService.GetSkippedInWindow(habit, monday, habit.Logs, weekStartDay: 1);

        skipped.Should().Be(1);
    }

    [Fact]
    public void GetRemainingCompletions_SkipsReduceTarget()
    {
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateHabit(FrequencyUnit.Week, 3, dueDate: monday, isFlexible: true);

        habit.SkipFlexible(monday);
        var remaining = HabitScheduleService.GetRemainingCompletions(habit, monday, habit.Logs, weekStartDay: 1);

        remaining.Should().Be(2);    }

    [Fact]
    public void HasCompletedLogInRange_CompletionLogInRange_ReturnsTrue()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 1, dueDate: Anchor);
        habit.Log(Anchor, advanceDueDate: false);

        HabitScheduleService.HasCompletedLogInRange(habit, Anchor, Anchor).Should().BeTrue();
    }

    [Fact]
    public void HasCompletedLogInRange_SkipLog_ReturnsFalse()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 3, dueDate: Anchor, isFlexible: true);
        habit.SkipFlexible(Anchor);
        HabitScheduleService.HasCompletedLogInRange(habit, Anchor, Anchor).Should().BeFalse();
    }

    [Fact]
    public void HasCompletedLogInRange_LogOutsideRange_ReturnsFalse()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 1, dueDate: Anchor);
        habit.Log(Anchor, advanceDueDate: false);

        HabitScheduleService.HasCompletedLogInRange(habit, Anchor.AddDays(1), Anchor.AddDays(5))
            .Should().BeFalse();
    }

    [Fact]
    public void HasCompletedLogInRange_OneTimeTaskCompletedBeforeRange_IgnoresStickyFlag()
    {
        var task = CreateHabit(null, null, dueDate: Anchor);
        task.Log(Anchor);
        task.IsCompleted.Should().BeTrue();
        HabitScheduleService.HasCompletedLogInRange(task, Anchor.AddDays(1), Anchor.AddDays(5))
            .Should().BeFalse();
    }

    [Fact]
    public void IsOverdueOnDate_OneTimeTaskPastDueNotCompleted_ReturnsTrue()
    {
        var task = CreateHabit(null, null, dueDate: Anchor);

        HabitScheduleService.IsOverdueOnDate(task, Anchor.AddDays(3)).Should().BeTrue();
    }

    [Fact]
    public void IsOverdueOnDate_OneTimeTaskCompleted_ReturnsFalse()
    {
        var task = CreateHabit(null, null, dueDate: Anchor);
        task.Log(Anchor);
        HabitScheduleService.IsOverdueOnDate(task, Anchor.AddDays(3)).Should().BeFalse();
    }

    [Fact]
    public void IsOverdueOnDate_OneTimeTaskDueInFuture_ReturnsFalse()
    {
        var task = CreateHabit(null, null, dueDate: Anchor.AddDays(5));

        HabitScheduleService.IsOverdueOnDate(task, Anchor).Should().BeFalse();
    }

    [Fact]
    public void IsOverdueOnDate_RecurringMissedPastOccurrence_ReturnsTrue()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 1, dueDate: Anchor);        var laterWednesday = Anchor.AddDays(9);
        HabitScheduleService.IsOverdueOnDate(habit, laterWednesday).Should().BeTrue();
    }

    [Fact]
    public void IsOverdueOnDate_RecurringDueOnReferenceDay_ReturnsFalse()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 1, dueDate: Anchor);

        HabitScheduleService.IsOverdueOnDate(habit, Anchor.AddDays(3)).Should().BeFalse();
    }

    [Fact]
    public void IsOverdueOnDate_RecurringDueToday_ReturnsFalse()
    {
        var habit = CreateHabit(FrequencyUnit.Day, 1, dueDate: Anchor);

        HabitScheduleService.IsOverdueOnDate(habit, Anchor).Should().BeFalse();
    }

    [Fact]
    public void IsOverdueOnDate_RecurringDueInFuture_ReturnsFalse()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 1, dueDate: Anchor.AddDays(7));

        HabitScheduleService.IsOverdueOnDate(habit, Anchor).Should().BeFalse();
    }

    [Fact]
    public void IsOverdueOnDate_FlexibleHabit_ReturnsFalse()
    {
        var habit = CreateHabit(FrequencyUnit.Week, 3, dueDate: Anchor, isFlexible: true);

        HabitScheduleService.IsOverdueOnDate(habit, Anchor.AddDays(30)).Should().BeFalse();
    }

    [Fact]
    public void IsOverdueOnDate_BadHabit_ReturnsFalse()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Bad", FrequencyUnit.Day, 1, IsBadHabit: true, DueDate: Anchor)).Value;

        HabitScheduleService.IsOverdueOnDate(habit, Anchor.AddDays(5)).Should().BeFalse();
    }

    [Fact]
    public async Task AdvanceStaleBadHabitDueDates_StaleDailyBadHabit_LandsOnTodayAndStaysDue()
    {
        var today = new DateOnly(2026, 4, 3);
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Bad", FrequencyUnit.Day, 1, IsBadHabit: true, DueDate: today.AddDays(-1))).Value;
        var (repo, unitOfWork) = SetupRepositoryReturning(habit);

        await HabitScheduleService.AdvanceStaleBadHabitDueDates(repo, unitOfWork, UserId, today, CancellationToken.None);

        habit.DueDate.Should().Be(today);
        HabitScheduleService.IsHabitDueOnDate(habit, today).Should().BeTrue();
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStaleBadHabitDueDates_AlreadyDueToday_DoesNotOvershoot()
    {
        var today = new DateOnly(2026, 4, 3);
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Bad", FrequencyUnit.Day, 1, IsBadHabit: true, DueDate: today)).Value;
        var (repo, unitOfWork) = SetupRepositoryReturning(habit);

        await HabitScheduleService.AdvanceStaleBadHabitDueDates(repo, unitOfWork, UserId, today, CancellationToken.None);

        habit.DueDate.Should().Be(today);
        HabitScheduleService.IsHabitDueOnDate(habit, today).Should().BeTrue();
    }

    private static List<DateOnly> GetLegacyScheduledDates(Habit habit, DateOnly from, DateOnly to)
    {
        var dates = new List<DateOnly>();
        for (var target = from; target <= to; target = target.AddDays(1))
        {
            if (habit.EndDate.HasValue && target > habit.EndDate.Value)
                continue;
            if (habit.IsFlexible)
            {
                if (habit.FrequencyUnit is not null && target >= habit.DueDate)
                    dates.Add(target);
                continue;
            }
            if (habit.FrequencyUnit is null)
            {
                if (target == habit.DueDate)
                    dates.Add(target);
                continue;
            }
            if (target < habit.DueDate)
                continue;
            if (habit.Days.Count > 0 && !habit.Days.Contains(target.DayOfWeek))
                continue;

            var quantity = habit.FrequencyQuantity ?? 1;
            var matches = habit.FrequencyUnit switch
            {
                FrequencyUnit.Day => quantity == 1
                    || LegacyTrueMod(target.DayNumber - habit.DueDate.DayNumber, quantity) == 0,
                FrequencyUnit.Week => target.DayOfWeek == habit.DueDate.DayOfWeek
                    && LegacyTrueMod((target.DayNumber - habit.DueDate.DayNumber) / 7, quantity) == 0,
                FrequencyUnit.Month => target.Day == Math.Min(
                        habit.DueDate.Day,
                        DateTime.DaysInMonth(target.Year, target.Month))
                    && LegacyTrueMod(
                        (target.Year - habit.DueDate.Year) * 12 + target.Month - habit.DueDate.Month,
                        quantity) == 0,
                FrequencyUnit.Year => IsLegacyYearlyMatch(target, habit.DueDate, quantity),
                _ => false
            };
            if (matches)
                dates.Add(target);
        }
        return dates;
    }

    private static bool IsLegacyYearlyMatch(DateOnly target, DateOnly anchor, int quantity)
    {
        if (LegacyTrueMod(target.Year - anchor.Year, quantity) != 0)
            return false;
        if (target.Month == anchor.Month && target.Day == anchor.Day)
            return true;
        return anchor.Month == 2
            && anchor.Day == 29
            && target.Month == 2
            && target.Day == 28
            && !DateTime.IsLeapYear(target.Year);
    }

    private static int LegacyTrueMod(int value, int modulus) =>
        ((value % modulus) + modulus) % modulus;

    private static (IGenericRepository<Habit> Repo, IUnitOfWork UnitOfWork) SetupRepositoryReturning(params Habit[] habits)
    {
        var repo = Substitute.For<IGenericRepository<Habit>>();
        repo.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(habits.ToList().AsReadOnly());
        return (repo, Substitute.For<IUnitOfWork>());
    }
}
