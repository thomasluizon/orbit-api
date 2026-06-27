using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

/// <summary>
/// Guards that a soft-deleted habit's preserved logs never leak into any aggregate read. Each test
/// reproduces a real log-reading query's shape against an in-memory context so the actual EF global
/// query filter (not a mock) decides exclusion. The two shapes in production are: ids materialised
/// from the filtered Habits set then fed to a direct HabitLogs query (gamification, streak, daily
/// summary, export, referral, get-all-logs), and Include of the Logs navigation off a Habits query
/// (calendar, retrospective, habit metrics, goal metrics).
/// </summary>
public class SoftDeleteLeakTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    [Fact]
    public async Task HabitsQuery_ExcludesSoftDeletedHabit()
    {
        await using var context = CreateInMemoryDbContext();
        var live = MakeHabitWithLog(softDeleted: false);
        var deleted = MakeHabitWithLog(softDeleted: true);
        context.Habits.AddRange(live, deleted);
        await context.SaveChangesAsync();

        var ids = await context.Habits.Where(h => h.UserId == UserId).Select(h => h.Id).ToListAsync();

        ids.Should().Contain(live.Id).And.NotContain(deleted.Id);
    }

    [Fact]
    public async Task GamificationAndStreakShape_HabitIdsContains_ExcludesSoftDeletedHabitLogs()
    {
        await using var context = CreateInMemoryDbContext();
        var live = MakeHabitWithLog(softDeleted: false);
        var deleted = MakeHabitWithLog(softDeleted: true);
        context.Habits.AddRange(live, deleted);
        await context.SaveChangesAsync();

        var habitIds = await context.Habits
            .Where(h => h.UserId == UserId)
            .Select(h => h.Id)
            .ToListAsync();
        var logs = await context.HabitLogs
            .Where(l => habitIds.Contains(l.HabitId) && l.Value > 0)
            .ToListAsync();

        logs.Should().Contain(l => l.HabitId == live.Id);
        logs.Should().NotContain(l => l.HabitId == deleted.Id);
    }

    [Fact]
    public async Task DailySummaryShape_BadHabitIdsContains_ExcludesSoftDeletedHabitLogs()
    {
        await using var context = CreateInMemoryDbContext();
        var live = MakeHabitWithLog(softDeleted: false, isBadHabit: true);
        var deleted = MakeHabitWithLog(softDeleted: true, isBadHabit: true);
        context.Habits.AddRange(live, deleted);
        await context.SaveChangesAsync();

        var badHabitIds = await context.Habits
            .Where(h => h.UserId == UserId && h.IsBadHabit)
            .Select(h => h.Id)
            .ToListAsync();
        var logs = await context.HabitLogs
            .Where(l => badHabitIds.Contains(l.HabitId) && l.Value > 0 && l.Date <= Today)
            .ToListAsync();

        badHabitIds.Should().NotContain(deleted.Id);
        logs.Should().NotContain(l => l.HabitId == deleted.Id);
    }

    [Fact]
    public async Task CalendarShape_IncludeLogs_ExcludesSoftDeletedHabitLogs()
    {
        await using var context = CreateInMemoryDbContext();
        var live = MakeHabitWithLog(softDeleted: false);
        var deleted = MakeHabitWithLog(softDeleted: true);
        context.Habits.AddRange(live, deleted);
        await context.SaveChangesAsync();

        var habits = await context.Habits
            .Where(h => h.UserId == UserId)
            .Include(h => h.Logs.Where(l => l.Date >= Today && l.Date <= Today))
            .ToListAsync();

        habits.Select(h => h.Id).Should().Contain(live.Id).And.NotContain(deleted.Id);
        habits.SelectMany(h => h.Logs).Should().NotContain(l => l.HabitId == deleted.Id);
        habits.SelectMany(h => h.Logs).Should().Contain(l => l.HabitId == live.Id);
    }

    [Fact]
    public async Task RetrospectiveShape_IncludeLogs_ExcludesSoftDeletedHabitLogs()
    {
        await using var context = CreateInMemoryDbContext();
        var live = MakeHabitWithLog(softDeleted: false);
        var deleted = MakeHabitWithLog(softDeleted: true);
        context.Habits.AddRange(live, deleted);
        await context.SaveChangesAsync();

        var habits = await context.Habits
            .Where(h => h.UserId == UserId)
            .Include(h => h.Logs)
            .ToListAsync();

        habits.SelectMany(h => h.Logs).Should().NotContain(l => l.HabitId == deleted.Id);
    }

    [Fact]
    public async Task HabitMetricsShape_HabitByIdIncludeLogs_ReturnsNothingForSoftDeletedHabit()
    {
        await using var context = CreateInMemoryDbContext();
        var deleted = MakeHabitWithLog(softDeleted: true);
        context.Habits.Add(deleted);
        await context.SaveChangesAsync();

        var habit = await context.Habits
            .Where(h => h.Id == deleted.Id)
            .Include(h => h.Logs)
            .FirstOrDefaultAsync();

        habit.Should().BeNull();
    }

    [Fact]
    public async Task CascadeSoftDeletedSubHabit_LogsExcludedFromHabitIdsAggregates()
    {
        await using var context = CreateInMemoryDbContext();
        var deletedAt = DateTime.UtcNow;
        var parent = Habit.Create(new HabitCreateParams(
            UserId, "Parent", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        parent.SoftDelete(deletedAt);
        var child = Habit.Create(new HabitCreateParams(
            UserId, "Child", FrequencyUnit.Day, 1, DueDate: Today, ParentHabitId: parent.Id)).Value;
        child.Log(Today);
        child.SoftDelete(deletedAt);
        context.Habits.AddRange(parent, child);
        await context.SaveChangesAsync();

        var habitIds = await context.Habits
            .Where(h => h.UserId == UserId)
            .Select(h => h.Id)
            .ToListAsync();
        var logs = await context.HabitLogs
            .Where(l => habitIds.Contains(l.HabitId))
            .ToListAsync();

        habitIds.Should().NotContain(child.Id);
        logs.Should().NotContain(l => l.HabitId == child.Id);
    }

    [Fact]
    public async Task SoftDeletedHabitLogs_RemainInTable_ForRestore()
    {
        await using var context = CreateInMemoryDbContext();
        var deleted = MakeHabitWithLog(softDeleted: true);
        context.Habits.Add(deleted);
        await context.SaveChangesAsync();

        var preserved = await context.HabitLogs
            .IgnoreQueryFilters()
            .Where(l => l.HabitId == deleted.Id)
            .ToListAsync();

        preserved.Should().ContainSingle();
    }

    private static Habit MakeHabitWithLog(bool softDeleted, bool isBadHabit = false)
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, isBadHabit ? "Bad" : "Habit", FrequencyUnit.Day, 1, DueDate: Today, IsBadHabit: isBadHabit)).Value;
        habit.Log(Today);
        if (softDeleted)
            habit.SoftDelete();
        return habit;
    }

    private static OrbitDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"SoftDeleteLeakTests_{Guid.NewGuid()}")
            .Options;
        return new OrbitDbContext(options);
    }
}
