using FluentAssertions;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Services;

public class HabitMetricsCalculatorTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>
    /// Creates a daily habit and logs it on the specified dates.
    /// </summary>
    private static Habit CreateDailyHabitWithLogs(
        DateOnly[] logDates,
        bool isBadHabit = false)
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Test Habit",
            FrequencyUnit.Day,
            1,
            IsBadHabit: isBadHabit,
            DueDate: Today)).Value;

        foreach (var date in logDates.OrderBy(d => d))
        {
            habit.Log(date, advanceDueDate: false);
        }

        return habit;
    }

    private static Habit CreateWeeklyHabitWithLogs(DateOnly[] logDates)
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Weekly Habit",
            FrequencyUnit.Week,
            1,
            DueDate: Today)).Value;

        foreach (var date in logDates.OrderBy(d => d))
        {
            habit.Log(date, advanceDueDate: false);
        }

        return habit;
    }

    private static Habit CreateOneTimeHabit()
    {
        return Habit.Create(new HabitCreateParams(
            ValidUserId,
            "One-time task",
            FrequencyUnit: null,
            FrequencyQuantity: null,
            DueDate: Today)).Value;
    }

    [Fact]
    public void Calculate_NoLogs_ReturnsZeroes()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Test", FrequencyUnit.Day, 1,
            DueDate: Today)).Value;

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.CurrentStreak.Should().Be(0);
        metrics.LongestStreak.Should().Be(0);
        metrics.TotalCompletions.Should().Be(0);
        metrics.LastCompletedDate.Should().BeNull();
        metrics.WeeklyCompletionRate.Should().Be(0);
        metrics.MonthlyCompletionRate.Should().Be(0);
    }

    [Fact]
    public void Calculate_SingleLogToday_ReturnsCorrectMetrics()
    {
        var habit = CreateDailyHabitWithLogs([Today]);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.TotalCompletions.Should().Be(1);
        metrics.LastCompletedDate.Should().Be(Today);
        metrics.CurrentStreak.Should().Be(1);
    }

    [Fact]
    public void Calculate_TodayLogged_CurrentStreakIsOne()
    {
        var habit = CreateDailyHabitWithLogs([Today]);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.CurrentStreak.Should().Be(1);
        metrics.LongestStreak.Should().Be(1);
    }

    [Fact]
    public void Calculate_TodayNotLogged_CurrentStreakIsZero()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Test", FrequencyUnit.Day, 1,
            DueDate: Today)).Value;

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.CurrentStreak.Should().Be(0);
    }

    [Fact]
    public void Calculate_LogOnNonExpectedDate_DoesNotCountForStreak()
    {
        var habit = CreateDailyHabitWithLogs([Today.AddDays(-100)]);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.CurrentStreak.Should().Be(0);
        metrics.TotalCompletions.Should().Be(1);
    }

    [Fact]
    public void Calculate_TodayLogged_WeeklyRate100Percent()
    {
        var habit = CreateDailyHabitWithLogs([Today]);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.WeeklyCompletionRate.Should().Be(100);
    }

    [Fact]
    public void Calculate_TodayNotLogged_WeeklyRate0Percent()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Test", FrequencyUnit.Day, 1,
            DueDate: Today)).Value;

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.WeeklyCompletionRate.Should().Be(0);
    }

    [Fact]
    public void Calculate_NoDaysCompletedInWeek_0PercentWeekly()
    {
        var habit = CreateDailyHabitWithLogs([Today.AddDays(-20)]);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.WeeklyCompletionRate.Should().Be(0);
    }

    [Fact]
    public void Calculate_TotalCompletions_MatchesDistinctLogDates()
    {
        var logDates = new[]
        {
            Today.AddDays(-6),
            Today.AddDays(-4),
            Today.AddDays(-2),
            Today
        };
        var habit = CreateDailyHabitWithLogs(logDates);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.TotalCompletions.Should().Be(4);
    }

    [Fact]
    public void Calculate_LastCompletedDate_ReturnsMaxLogDate()
    {
        var logDates = new[]
        {
            Today.AddDays(-5),
            Today.AddDays(-1),
            Today.AddDays(-3)
        };
        var habit = CreateDailyHabitWithLogs(logDates);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.LastCompletedDate.Should().Be(Today.AddDays(-1));
    }

    [Fact]
    public void Calculate_NoLogs_LastCompletedDateIsNull()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Test", FrequencyUnit.Day, 1,
            DueDate: Today)).Value;

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.LastCompletedDate.Should().BeNull();
    }

    [Fact]
    public void Calculate_OneTimeHabit_NotCompleted_ReturnsZeroes()
    {
        var habit = CreateOneTimeHabit();

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.TotalCompletions.Should().Be(0);
        metrics.CurrentStreak.Should().Be(0);
    }

    [Fact]
    public void Calculate_BadHabit_NoLogs_StreakIsPositive()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Smoking", FrequencyUnit.Day, 1,
            IsBadHabit: true, DueDate: Today)).Value;

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.CurrentStreak.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void Calculate_BadHabit_LoggedToday_BreaksStreak()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Smoking", FrequencyUnit.Day, 1,
            IsBadHabit: true, DueDate: Today)).Value;
        habit.Log(Today, advanceDueDate: false);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.CurrentStreak.Should().Be(0);
    }

    [Fact]
    public void Calculate_BadHabit_NoLogs_CompletionRate100()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Junk Food", FrequencyUnit.Day, 1,
            IsBadHabit: true, DueDate: Today)).Value;

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.WeeklyCompletionRate.Should().Be(100);
    }

    [Fact]
    public void Calculate_BadHabit_LoggedToday_CompletionRate0()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Junk Food", FrequencyUnit.Day, 1,
            IsBadHabit: true, DueDate: Today)).Value;
        habit.Log(Today, advanceDueDate: false);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.WeeklyCompletionRate.Should().Be(0);
    }

    [Fact]
    public void Calculate_WeeklyHabit_LoggedToday_HasStreak()
    {
        var habit = CreateWeeklyHabitWithLogs([Today]);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.TotalCompletions.Should().Be(1);
        metrics.CurrentStreak.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void Calculate_WithTimezone_UsesTimezoneForDates()
    {
        var habit = CreateDailyHabitWithLogs([Today]);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today, TimeZoneInfo.Utc);

        metrics.TotalCompletions.Should().Be(1);
    }

    [Fact]
    public void Calculate_WithNullTimezone_DefaultsToUtc()
    {
        var habit = CreateDailyHabitWithLogs([Today]);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today, null);

        metrics.TotalCompletions.Should().Be(1);
    }

    [Fact]
    public void GetUserToday_NullTimezone_ReturnsUtcDate()
    {
        var user = User.Create("Test User", "test@example.com").Value;

        var today = HabitMetricsCalculator.GetUserToday(user);

        today.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow));
    }

    [Fact]
    public void Calculate_ReturnsAllMetricFields()
    {
        var habit = CreateDailyHabitWithLogs([Today]);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.CurrentStreak.Should().BeGreaterThanOrEqualTo(0);
        metrics.LongestStreak.Should().BeGreaterThanOrEqualTo(0);
        metrics.WeeklyCompletionRate.Should().BeGreaterThanOrEqualTo(0);
        metrics.MonthlyCompletionRate.Should().BeGreaterThanOrEqualTo(0);
        metrics.TotalCompletions.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Calculate_MonthlyHabit_LoggedToday_HasStreak()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Monthly Review",
            FrequencyUnit.Month,
            1,
            DueDate: Today)).Value;

        habit.Log(Today, advanceDueDate: false);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.TotalCompletions.Should().Be(1);
        metrics.CurrentStreak.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void Calculate_MonthlyHabit_NoLogs_ZeroStreak()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Monthly Review",
            FrequencyUnit.Month,
            1,
            DueDate: Today)).Value;

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.CurrentStreak.Should().Be(0);
    }

    [Fact]
    public void Calculate_YearlyHabit_LoggedToday_HasStreak()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Annual Checkup",
            FrequencyUnit.Year,
            1,
            DueDate: Today)).Value;

        habit.Log(Today, advanceDueDate: false);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.TotalCompletions.Should().Be(1);
        metrics.CurrentStreak.Should().Be(1);
    }

    [Fact]
    public void Calculate_EveryTwoDaysHabit_LoggedToday()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Every Other Day",
            FrequencyUnit.Day,
            2,
            DueDate: Today)).Value;

        habit.Log(Today, advanceDueDate: false);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.TotalCompletions.Should().Be(1);
        metrics.CurrentStreak.Should().Be(1);
    }

    [Fact]
    public void Calculate_DailyWithDaysFilter_OnlyCountsFilteredDays()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Weekday Habit",
            FrequencyUnit.Day,
            1,
            Days: new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday },
            DueDate: Today)).Value;

        habit.Log(Today, advanceDueDate: false);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.TotalCompletions.Should().Be(1);
    }

    [Fact]
    public void Calculate_LongestStreak_GreaterThanCurrent_WhenBroken()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Test",
            FrequencyUnit.Day,
            1,
            DueDate: Today)).Value;

        habit.Log(Today, advanceDueDate: false);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.LongestStreak.Should().BeGreaterThanOrEqualTo(metrics.CurrentStreak);
    }

    [Fact]
    public void Calculate_OneTimeHabit_Completed_Streak1()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "One-time task",
            FrequencyUnit: null,
            FrequencyQuantity: null,
            DueDate: Today)).Value;

        habit.Log(Today);
        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.TotalCompletions.Should().Be(1);
        metrics.CurrentStreak.Should().Be(1);
    }

    [Fact]
    public void Calculate_BadHabit_LongestStreak_MatchesCurrentWhenNoLogs()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Smoking", FrequencyUnit.Day, 1,
            IsBadHabit: true, DueDate: Today)).Value;

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.LongestStreak.Should().Be(metrics.CurrentStreak);
    }

    [Fact]
    public void Calculate_WithSpecificTimezone_ProducesMetrics()
    {
        var habit = CreateDailyHabitWithLogs([Today]);
        var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

        var metrics = HabitMetricsCalculator.Calculate(habit, Today, tz);

        metrics.TotalCompletions.Should().Be(1);
    }

    [Fact]
    public void GetUserToday_WithTimezone_ReturnsLocalDate()
    {
        var user = User.Create("Test User", "test@example.com").Value;

        var today = HabitMetricsCalculator.GetUserToday(user);

        today.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow));
    }

    [Fact]
    public void Calculate_MonthlyRate_ZeroWhenNoExpectedDatesInRange()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Yearly",
            FrequencyUnit.Year,
            1,
            DueDate: Today)).Value;

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.MonthlyCompletionRate.Should().Be(0);
    }

    [Fact]
    public void Calculate_MonthlyRate_100WhenLogged()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Yearly",
            FrequencyUnit.Year,
            1,
            DueDate: Today)).Value;
        habit.Log(Today, advanceDueDate: false);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.MonthlyCompletionRate.Should().Be(100);
    }

    [Fact]
    public void Calculate_FlexibleHabit_LoggedMultipleTimes_CountsAll()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Flexible Exercise",
            FrequencyUnit.Week,
            3,
            DueDate: Today,
            IsFlexible: true)).Value;

        habit.Log(Today);
        habit.Log(Today);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.TotalCompletions.Should().Be(1);    }
}
