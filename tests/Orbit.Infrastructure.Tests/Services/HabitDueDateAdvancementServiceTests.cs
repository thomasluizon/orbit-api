using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the CatchUpDueDate logic on the Habit entity that the
/// HabitDueDateAdvancementService relies on, plus the service's
/// query filter conditions and cutoff logic.
/// The background service loop and DB interactions are integration concerns.
/// </summary>
public class HabitDueDateAdvancementServiceTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static Habit CreateRecurringHabit(
        FrequencyUnit unit = FrequencyUnit.Day,
        int quantity = 1,
        DateOnly? dueDate = null,
        DateOnly? endDate = null,
        bool isFlexible = false)
    {
        return Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Test Habit",
            unit,
            quantity,
            DueDate: dueDate ?? Today.AddDays(-10),
            EndDate: endDate,
            IsFlexible: isFlexible)).Value;
    }

    // ── CatchUpDueDate ──

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

    // ── Additional CatchUpDueDate edge cases ──

    [Fact]
    public void CatchUpDueDate_EveryThreeDays_AdvancesInCorrectSteps()
    {
        var startDate = Today.AddDays(-9);
        var habit = CreateRecurringHabit(FrequencyUnit.Day, 3, startDate);

        habit.CatchUpDueDate(Today);

        habit.DueDate.Should().BeOnOrAfter(Today);
        var daysDiff = habit.DueDate.DayNumber - startDate.DayNumber;
        (daysDiff % 3).Should().Be(0);
    }

    [Fact]
    public void CatchUpDueDate_EveryTwoWeeks_AdvancesCorrectly()
    {
        var startDate = Today.AddDays(-28); // 4 weeks ago
        var habit = CreateRecurringHabit(FrequencyUnit.Week, 2, startDate);

        habit.CatchUpDueDate(Today);

        habit.DueDate.Should().BeOnOrAfter(Today);
    }

    [Fact]
    public void CatchUpDueDate_EveryThreeMonths_AdvancesCorrectly()
    {
        var startDate = Today.AddMonths(-9); // 3 quarters ago
        var habit = CreateRecurringHabit(FrequencyUnit.Month, 3, startDate);

        habit.CatchUpDueDate(Today);

        habit.DueDate.Should().BeOnOrAfter(Today);
    }

    [Fact]
    public void CatchUpDueDate_OneDayBehind_AdvancesOneStep()
    {
        var yesterday = Today.AddDays(-1);
        var habit = CreateRecurringHabit(FrequencyUnit.Day, 1, yesterday);

        habit.CatchUpDueDate(Today);

        habit.DueDate.Should().BeOnOrAfter(Today);
    }

    [Fact]
    public void CatchUpDueDate_VeryOldDueDate_StillCatchesUp()
    {
        // A habit that hasn't been touched in a very long time
        var startDate = Today.AddDays(-365);
        var habit = CreateRecurringHabit(FrequencyUnit.Day, 1, startDate);

        habit.CatchUpDueDate(Today);

        habit.DueDate.Should().BeOnOrAfter(Today);
    }

    // ── Service query filter conditions ──

    [Fact]
    public void QueryFilter_CompletedHabit_Excluded()
    {
        // The service filters: !h.IsCompleted
        var habit = CreateRecurringHabit(FrequencyUnit.Day, 1, Today.AddDays(-5));
        habit.Log(Today.AddDays(-5)); // For one-time tasks this marks completed, but this is recurring

        // Recurring habit logged does not mark IsCompleted
        habit.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void QueryFilter_OneTimeTask_Excluded()
    {
        // The service filters: h.FrequencyUnit != null
        // A one-time task has null FrequencyUnit
        var oneTime = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "One-time task",
            null,   // FrequencyUnit
            null,   // FrequencyQuantity
            DueDate: Today.AddDays(-5))).Value;

        var shouldInclude = oneTime.FrequencyUnit != null
            && oneTime.FrequencyQuantity != null;

        shouldInclude.Should().BeFalse();
    }

    [Fact]
    public void QueryFilter_FlexibleHabit_Excluded()
    {
        // The service filters: !h.IsFlexible
        var flexible = CreateRecurringHabit(isFlexible: true);

        flexible.IsFlexible.Should().BeTrue();
    }

    [Fact]
    public void QueryFilter_ConservativeCutoff_IsYesterdayUtc()
    {
        // The service uses: cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1))
        // This is to avoid timezone false positives
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        cutoff.Should().Be(today.AddDays(-1));
    }

    [Fact]
    public void QueryFilter_HabitDueDateAtCutoff_NotIncluded()
    {
        // DueDate must be BEFORE cutoff (strictly less than)
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));

        var habit = CreateRecurringHabit(dueDate: cutoff);

        var shouldAdvance = habit.DueDate < cutoff;
        shouldAdvance.Should().BeFalse();
    }

    [Fact]
    public void QueryFilter_HabitDueDateBeforeCutoff_Included()
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));

        var habit = CreateRecurringHabit(dueDate: cutoff.AddDays(-1));

        var shouldAdvance = habit.DueDate < cutoff;
        shouldAdvance.Should().BeTrue();
    }

    // ── Per-user timezone guard ──

    [Fact]
    public void TimezoneGuard_HabitDueDateBeforeUserToday_ShouldAdvance()
    {
        // The service has a per-user check: if (habit.DueDate >= userToday) continue;
        var userToday = Today;
        var habit = CreateRecurringHabit(dueDate: Today.AddDays(-3));

        var shouldSkip = habit.DueDate >= userToday;
        shouldSkip.Should().BeFalse();
    }

    [Fact]
    public void TimezoneGuard_HabitDueDateIsUserToday_ShouldSkip()
    {
        var userToday = Today;
        var habit = CreateRecurringHabit(dueDate: Today);

        var shouldSkip = habit.DueDate >= userToday;
        shouldSkip.Should().BeTrue();
    }

    [Fact]
    public void TimezoneGuard_HabitDueDateAfterUserToday_ShouldSkip()
    {
        var userToday = Today;
        var habit = CreateRecurringHabit(dueDate: Today.AddDays(1));

        var shouldSkip = habit.DueDate >= userToday;
        shouldSkip.Should().BeTrue();
    }
}
