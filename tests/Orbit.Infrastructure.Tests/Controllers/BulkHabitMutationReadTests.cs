using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Controllers;

public class BulkHabitMutationReadTests
{
    private static readonly DateOnly Today = new(2026, 6, 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Skip_PretrackedHabit_RefreshesOrRejectsStalePendingChanges(bool pendingChanges)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("User", "tracked-bulk@example.test").Value;
        var habit = Habit.Create(new(HabitSchedule.Recurring, user.Id, "Daily", FrequencyUnit.Day, 1, Today)).Value;
        db.AddRange(user, habit);
        await db.SaveChangesAsync();
        if (pendingChanges) habit.SetPosition(2);
        await using (var other = factory.CreateContext())
        {
            var updated = await other.Habits.SingleAsync();
            updated.AdvanceDueDate(Today);
            updated.UpdatedAtUtc = updated.UpdatedAtUtc.AddSeconds(1);
            await other.SaveChangesAsync();
        }
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(Today);
        dates.GetUserWeekStartDayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(1);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var unit = new UnitOfWork(db, new DatabaseConnectionSettings());
        var result = await new BulkSkipHabitsCommandHandler(new GenericRepository<Habit>(db), new GenericRepository<HabitLog>(db),
            dates, unit, cache, new GenericRepository<Notification>(db)).Handle(new(user.Id, [new(habit.Id)]), default);

        if (pendingChanges)
        {
            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be(ErrorMessages.ConcurrentUpdateConflict.Code);
        }
        else
        {
            result.IsSuccess.Should().BeTrue();
            result.Value.Results.Should().ContainSingle(item => item.Status == BulkItemStatus.Failed
                && item.ErrorCode == ErrorMessages.HabitNotYetDue.Code);
            habit.NextDueDate.Should().Be(Today.AddDays(1));
        }
    }

    [Theory]
    [InlineData(FrequencyUnit.Month)]
    [InlineData(FrequencyUnit.Year)]
    public async Task FlexiblePeriod_CompletionsBeforeOverdueWindowStillLimitTheBatch(FrequencyUnit frequency)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("User", "flexible-bulk@example.test").Value;
        var today = new DateOnly(2026, 6, 20);
        var habit = Habit.Create(new(HabitSchedule.Flexible, user.Id, "Flexible", frequency, 1, today.AddYears(-2))).Value;
        var start = HabitScheduleService.GetWindowStart(habit, today, 1);
        habit.Log(start, advanceDueDate: false, loggedOnLocalDate: start).IsSuccess.Should().BeTrue();
        db.AddRange(user, habit);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await using var host = new BulkHabitsPipelineHost(factory.CreateContext, new(), today);
        var response = await host.SendAsync(user.Id, "skip", Body(habit.Id), "flexible-period");
        response.Response.StatusCode.Should().Be(200);
        response.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(response.Response.Body);
        json.RootElement.GetProperty("results")[0].GetProperty("status").GetString().Should().Be("Failed");
        (await db.HabitLogs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ParentBatch_UnselectedSiblingAndAncestors_DeriveOnlyAfterEveryChildSlips()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("User", "parent-bulk@example.test").Value;
        Habit Create(string title, Guid? parent = null) => Habit.Create(new(HabitSchedule.Recurring, user.Id, title,
            FrequencyUnit.Day, 1, Today, IsBadHabit: true, ParentHabitId: parent)).Value;
        var root = Create("Root");
        var parent = Create("Parent", root.Id);
        var first = Create("First", parent.Id);
        var second = Create("Second", parent.Id);
        db.AddRange(user, root, parent, first, second);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await using var host = new BulkHabitsPipelineHost(factory.CreateContext, new(), Today);

        (await host.SendAsync(user.Id, "log", Body(first.Id), "first-child")).Response.StatusCode.Should().Be(200);
        (await db.HabitLogs.CountAsync(log => log.HabitId == parent.Id || log.HabitId == root.Id)).Should().Be(0);
        (await host.SendAsync(user.Id, "log", Body(second.Id), "second-child")).Response.StatusCode.Should().Be(200);
        var slips = await db.HabitLogs.ToListAsync();
        slips.Should().HaveCount(4).And.OnlyContain(log => log.Date == Today && log.IsSlip);
        slips.Select(log => log.HabitId).Should().BeEquivalentTo([root.Id, parent.Id, first.Id, second.Id]);
    }

    private static string Body(Guid habitId) => JsonSerializer.Serialize(new { items = new[] { new { habitId } } });
}
