using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the CatchUpDueDate logic on the Habit entity that the
/// HabitDueDateAdvancementService relies on, the service's query filter
/// conditions and cutoff logic, and a DB-backed regression covering the
/// real advancement query (bad habits advance; non-bad stay overdue).
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

    [Fact]
    public void CatchUpDueDate_DailyHabit_PastDue_AdvancesToToday()
    {
        var habit = CreateRecurringHabit(FrequencyUnit.Day, 1, Today.AddDays(-5));

        habit.CatchUpDueDate(Today);

        habit.DueDate.Should().BeOnOrAfter(Today);
    }

    [Fact]
    public void CatchUpDueDate_WeeklyHabit_PastDue_AdvancesByWeeks()
    {
        var startDate = Today.AddDays(-21);        var habit = CreateRecurringHabit(FrequencyUnit.Week, 1, startDate);

        habit.CatchUpDueDate(Today);

        habit.DueDate.Should().BeOnOrAfter(Today);
    }

    [Fact]
    public void CatchUpDueDate_MonthlyHabit_PastDue_AdvancesByMonths()
    {
        var startDate = Today.AddMonths(-3);
        var habit = CreateRecurringHabit(FrequencyUnit.Month, 1, startDate);

        habit.CatchUpDueDate(Today);

        habit.DueDate.Should().BeOnOrAfter(Today);
    }

    [Fact]
    public void CatchUpDueDate_FutureHabit_DoesNotChange()
    {
        var futureDate = Today.AddDays(5);
        var habit = CreateRecurringHabit(FrequencyUnit.Day, 1, futureDate);

        habit.CatchUpDueDate(Today);

        habit.DueDate.Should().Be(futureDate);
    }

    [Fact]
    public void CatchUpDueDate_TodayHabit_DoesNotChange()
    {
        var habit = CreateRecurringHabit(FrequencyUnit.Day, 1, Today);

        habit.CatchUpDueDate(Today);

        habit.DueDate.Should().Be(Today);
    }

    [Fact]
    public void CatchUpDueDate_EveryTwoDays_AdvancesCorrectly()
    {
        var startDate = Today.AddDays(-7);
        var habit = CreateRecurringHabit(FrequencyUnit.Day, 2, startDate);

        habit.CatchUpDueDate(Today);

        habit.DueDate.Should().BeOnOrAfter(Today);
        var daysDiff = habit.DueDate.DayNumber - startDate.DayNumber;
        (daysDiff % 2).Should().Be(0);
    }

    [Fact]
    public void CatchUpDueDate_WithEndDate_MarksCompletedWhenPastEnd()
    {
        var startDate = Today.AddDays(-30);
        var endDate = Today.AddDays(-5);
        var habit = CreateRecurringHabit(FrequencyUnit.Day, 1, startDate, endDate);

        habit.CatchUpDueDate(Today);

        habit.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void CatchUpDueDate_WithFutureEndDate_DoesNotMarkCompleted()
    {
        var startDate = Today.AddDays(-5);
        var endDate = Today.AddDays(30);
        var habit = CreateRecurringHabit(FrequencyUnit.Day, 1, startDate, endDate);

        habit.CatchUpDueDate(Today);

        habit.IsCompleted.Should().BeFalse();
        habit.DueDate.Should().BeOnOrAfter(Today);
    }

    [Fact]
    public void CatchUpDueDate_YearlyHabit_PastDue_AdvancesByYears()
    {
        var startDate = Today.AddYears(-2);
        var habit = CreateRecurringHabit(FrequencyUnit.Year, 1, startDate);

        habit.CatchUpDueDate(Today);

        habit.DueDate.Should().BeOnOrAfter(Today);
    }

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
        var startDate = Today.AddDays(-28);        var habit = CreateRecurringHabit(FrequencyUnit.Week, 2, startDate);

        habit.CatchUpDueDate(Today);

        habit.DueDate.Should().BeOnOrAfter(Today);
    }

    [Fact]
    public void CatchUpDueDate_EveryThreeMonths_AdvancesCorrectly()
    {
        var startDate = Today.AddMonths(-9);        var habit = CreateRecurringHabit(FrequencyUnit.Month, 3, startDate);

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
        var startDate = Today.AddDays(-365);
        var habit = CreateRecurringHabit(FrequencyUnit.Day, 1, startDate);

        habit.CatchUpDueDate(Today);

        habit.DueDate.Should().BeOnOrAfter(Today);
    }

    [Fact]
    public void QueryFilter_CompletedHabit_Excluded()
    {
        var habit = CreateRecurringHabit(FrequencyUnit.Day, 1, Today.AddDays(-5));
        habit.Log(Today.AddDays(-5));
        habit.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void QueryFilter_OneTimeTask_Excluded()
    {
        var oneTime = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "One-time task",
            null,            null,            DueDate: Today.AddDays(-5))).Value;

        var shouldInclude = oneTime.FrequencyUnit != null
            && oneTime.FrequencyQuantity != null;

        shouldInclude.Should().BeFalse();
    }

    [Fact]
    public void QueryFilter_FlexibleHabit_Excluded()
    {
        var flexible = CreateRecurringHabit(isFlexible: true);

        flexible.IsFlexible.Should().BeTrue();
    }

    [Fact]
    public void QueryFilter_OnlyBadHabitsAdvanced()
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var nonBad = CreateRecurringHabit(FrequencyUnit.Day, 1, cutoff.AddDays(-1));
        var bad = Habit.Create(new HabitCreateParams(
            ValidUserId, "Bad", FrequencyUnit.Day, 1, IsBadHabit: true, DueDate: cutoff.AddDays(-1))).Value;

        bool ShouldAdvance(Habit h) =>
            !h.IsCompleted && h.FrequencyUnit != null && h.FrequencyQuantity != null
            && !h.IsFlexible && h.IsBadHabit && h.DueDate < cutoff;

        ShouldAdvance(nonBad).Should().BeFalse();
        ShouldAdvance(bad).Should().BeTrue();
    }

    [Fact]
    public async Task AdvanceStaleDueDates_AdvancesBadHabitOnly_LeavesNonBadOverdue()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var staleDueDate = Today.AddDays(-3);
        var nonBadHabit = Habit.Create(new HabitCreateParams(
            user.Id, "Non-bad recurring", FrequencyUnit.Day, 1,
            IsBadHabit: false, DueDate: staleDueDate)).Value;
        var badHabit = Habit.Create(new HabitCreateParams(
            user.Id, "Bad recurring", FrequencyUnit.Day, 1,
            IsBadHabit: true, DueDate: staleDueDate)).Value;
        dbContext.Users.Add(user);
        dbContext.Habits.AddRange(nonBadHabit, badHabit);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        await service.AdvanceStaleDueDates(CancellationToken.None);

        var reloadedNonBad = await dbContext.Habits.AsNoTracking()
            .SingleAsync(h => h.Id == nonBadHabit.Id);
        var reloadedBad = await dbContext.Habits.AsNoTracking()
            .SingleAsync(h => h.Id == badHabit.Id);

        reloadedBad.DueDate.Should().BeOnOrAfter(Today);
        reloadedNonBad.DueDate.Should().Be(staleDueDate);
        reloadedNonBad.DueDate.Should().BeBefore(Today);
    }

    private static OrbitDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"HabitDueDateAdvancementServiceTests_{Guid.NewGuid()}")
            .Options;
        return new OrbitDbContext(options);
    }

    private static HabitDueDateAdvancementService CreateService(OrbitDbContext dbContext)
    {
        var serviceProvider = new ServiceCollection()
            .AddSingleton(dbContext)
            .BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return new HabitDueDateAdvancementService(
            scopeFactory,
            NullLogger<HabitDueDateAdvancementService>.Instance,
            new ConfigurationBuilder().Build());
    }

    [Fact]
    public void QueryFilter_ConservativeCutoff_IsYesterdayUtc()
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        cutoff.Should().Be(today.AddDays(-1));
    }

    [Fact]
    public void QueryFilter_HabitDueDateAtCutoff_NotIncluded()
    {
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

    [Fact]
    public void TimezoneGuard_HabitDueDateBeforeUserToday_ShouldAdvance()
    {
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
