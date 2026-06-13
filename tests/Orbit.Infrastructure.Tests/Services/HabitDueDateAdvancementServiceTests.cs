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
        bool isFlexible = false,
        bool isBadHabit = false)
    {
        return Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Test Habit",
            unit,
            quantity,
            DueDate: dueDate ?? Today.AddDays(-10),
            EndDate: endDate,
            IsFlexible: isFlexible,
            IsBadHabit: isBadHabit)).Value;
    }

    private static Func<Habit, bool> StaleBadHabitFilterFor(DateOnly cutoff) =>
        HabitDueDateAdvancementService.StaleBadHabitFilter(cutoff).Compile();

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
        var cutoff = Today.AddDays(-1);
        var habit = CreateRecurringHabit(
            dueDate: cutoff.AddDays(-10), endDate: cutoff.AddDays(-5), isBadHabit: true);
        habit.CatchUpDueDate(cutoff.AddDays(-4));
        habit.IsCompleted.Should().BeTrue();
        habit.DueDate.Should().BeBefore(cutoff);

        StaleBadHabitFilterFor(cutoff)(habit).Should().BeFalse();
    }

    [Fact]
    public void QueryFilter_OneTimeTask_Excluded()
    {
        var cutoff = Today.AddDays(-1);
        var oneTime = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "One-time task",
            null,
            null,
            IsBadHabit: true,
            DueDate: cutoff.AddDays(-1))).Value;

        StaleBadHabitFilterFor(cutoff)(oneTime).Should().BeFalse();
    }

    [Fact]
    public void QueryFilter_FlexibleHabit_Excluded()
    {
        var cutoff = Today.AddDays(-1);
        var flexible = CreateRecurringHabit(
            dueDate: cutoff.AddDays(-1), isFlexible: true, isBadHabit: true);

        StaleBadHabitFilterFor(cutoff)(flexible).Should().BeFalse();
    }

    [Fact]
    public void QueryFilter_OnlyBadHabitsAdvanced()
    {
        var cutoff = Today.AddDays(-1);
        var nonBad = CreateRecurringHabit(dueDate: cutoff.AddDays(-1));
        var bad = CreateRecurringHabit(dueDate: cutoff.AddDays(-1), isBadHabit: true);

        var includesHabit = StaleBadHabitFilterFor(cutoff);

        includesHabit(nonBad).Should().BeFalse();
        includesHabit(bad).Should().BeTrue();
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
        var cutoff = HabitDueDateAdvancementService.ConservativeCutoffUtc();

        cutoff.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1));
    }

    [Fact]
    public void QueryFilter_HabitDueDateAtCutoff_NotIncluded()
    {
        var cutoff = Today.AddDays(-1);
        var habit = CreateRecurringHabit(dueDate: cutoff, isBadHabit: true);

        StaleBadHabitFilterFor(cutoff)(habit).Should().BeFalse();
    }

    [Fact]
    public void QueryFilter_HabitDueDateBeforeCutoff_Included()
    {
        var cutoff = Today.AddDays(-1);
        var habit = CreateRecurringHabit(dueDate: cutoff.AddDays(-1), isBadHabit: true);

        StaleBadHabitFilterFor(cutoff)(habit).Should().BeTrue();
    }

    [Fact]
    public void TimezoneGuard_HabitDueDateBeforeUserToday_ShouldAdvance()
    {
        var habit = CreateRecurringHabit(dueDate: Today.AddDays(-3));

        HabitDueDateAdvancementService.ShouldAdvanceForUserToday(habit, Today).Should().BeTrue();
    }

    [Fact]
    public void TimezoneGuard_HabitDueDateIsUserToday_ShouldSkip()
    {
        var habit = CreateRecurringHabit(dueDate: Today);

        HabitDueDateAdvancementService.ShouldAdvanceForUserToday(habit, Today).Should().BeFalse();
    }

    [Fact]
    public void TimezoneGuard_HabitDueDateAfterUserToday_ShouldSkip()
    {
        var habit = CreateRecurringHabit(dueDate: Today.AddDays(1));

        HabitDueDateAdvancementService.ShouldAdvanceForUserToday(habit, Today).Should().BeFalse();
    }
}
