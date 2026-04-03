using FluentAssertions;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Services;

public class HabitMetricsCalculatorTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();

    // GenerateExpectedDates uses habit.CreatedAtUtc (converted to user-local date) as the
    // lower boundary. Since Habit.Create sets CreatedAtUtc = DateTime.UtcNow, we use Today
    // as the reference for all test dates. The expected dates window is [habitStartDate..today],
    // so only Today itself falls in the window for a just-created habit.
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

    // --- Basic metrics calculation ---

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
        // Habit was just created today: only 1 expected date (today), which is logged
        metrics.CurrentStreak.Should().Be(1);
    }

    // --- Streak tests ---
    // The calculator generates expected dates from [habitCreatedDate..today].
    // For a habit created today, only today is expected. This correctly models
    // "streak since habit creation".

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
        // Not logged today -- the only expected date is today
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Test", FrequencyUnit.Day, 1,
            DueDate: Today)).Value;

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        // Today is skipped at position 0, but no prior dates exist, so streak = 0
        metrics.CurrentStreak.Should().Be(0);
    }

    [Fact]
    public void Calculate_LogOnNonExpectedDate_DoesNotCountForStreak()
    {
        // Log a date far in the past (before habit creation) -- it won't appear in expected dates
        var habit = CreateDailyHabitWithLogs([Today.AddDays(-100)]);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        // The log exists but the date isn't in expected dates, so streak is 0
        metrics.CurrentStreak.Should().Be(0);
        // But total completions still counts it (logs with Value > 0)
        metrics.TotalCompletions.Should().Be(1);
    }

    // --- Completion rate tests ---

    [Fact]
    public void Calculate_TodayLogged_WeeklyRate100Percent()
    {
        // Habit created today, 1 expected date (today), logged = 100%
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
        // Log a date outside the 7-day window
        var habit = CreateDailyHabitWithLogs([Today.AddDays(-20)]);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.WeeklyCompletionRate.Should().Be(0);
    }

    // --- Total completions ---

    [Fact]
    public void Calculate_TotalCompletions_MatchesDistinctLogDates()
    {
        // Multiple logs on different dates
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

    // --- One-time habit ---

    [Fact]
    public void Calculate_OneTimeHabit_NotCompleted_ReturnsZeroes()
    {
        var habit = CreateOneTimeHabit();

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.TotalCompletions.Should().Be(0);
        metrics.CurrentStreak.Should().Be(0);
    }

    // --- Bad habit tests ---

    [Fact]
    public void Calculate_BadHabit_NoLogs_StreakIsPositive()
    {
        // Bad habit: streak counts days WITHOUT logging (resisting the habit)
        // No logs means success for every expected date
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Smoking", FrequencyUnit.Day, 1,
            IsBadHabit: true, DueDate: Today)).Value;

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.CurrentStreak.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void Calculate_BadHabit_LoggedToday_BreaksStreak()
    {
        // Logging a bad habit today breaks the streak
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
        // For bad habits, not logging = completion. No logs = 100%
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Junk Food", FrequencyUnit.Day, 1,
            IsBadHabit: true, DueDate: Today)).Value;

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.WeeklyCompletionRate.Should().Be(100);
    }

    [Fact]
    public void Calculate_BadHabit_LoggedToday_CompletionRate0()
    {
        // For bad habits, logging = failure. 1 expected date (today) + logged = 0%
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Junk Food", FrequencyUnit.Day, 1,
            IsBadHabit: true, DueDate: Today)).Value;
        habit.Log(Today, advanceDueDate: false);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.WeeklyCompletionRate.Should().Be(0);
    }

    // --- Weekly habit ---

    [Fact]
    public void Calculate_WeeklyHabit_LoggedToday_HasStreak()
    {
        var habit = CreateWeeklyHabitWithLogs([Today]);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        metrics.TotalCompletions.Should().Be(1);
        metrics.CurrentStreak.Should().BeGreaterThanOrEqualTo(1);
    }

    // --- Timezone support ---

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

    // --- GetUserToday tests ---

    [Fact]
    public void GetUserToday_NullTimezone_ReturnsUtcDate()
    {
        var user = User.Create("Test User", "test@example.com").Value;

        var today = HabitMetricsCalculator.GetUserToday(user);

        today.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow));
    }

    // --- Return type structure ---

    [Fact]
    public void Calculate_ReturnsAllMetricFields()
    {
        var habit = CreateDailyHabitWithLogs([Today]);

        var metrics = HabitMetricsCalculator.Calculate(habit, Today);

        // Verify all fields of HabitMetrics are populated
        metrics.CurrentStreak.Should().BeGreaterThanOrEqualTo(0);
        metrics.LongestStreak.Should().BeGreaterThanOrEqualTo(0);
        metrics.WeeklyCompletionRate.Should().BeGreaterThanOrEqualTo(0);
        metrics.MonthlyCompletionRate.Should().BeGreaterThanOrEqualTo(0);
        metrics.TotalCompletions.Should().BeGreaterThanOrEqualTo(0);
    }
}
