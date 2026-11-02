using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class BoundedHandlerRoundTripTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    [Theory]
    [InlineData("schedule")]
    [InlineData("detail")]
    [InlineData("logs")]
    [InlineData("all-logs")]
    [InlineData("metrics")]
    [InlineData("period")]
    [InlineData("calendar")]
    [InlineData("widget")]
    [InlineData("trends")]
    [InlineData("reschedule")]
    [InlineData("count")]
    [InlineData("streak")]
    [InlineData("retrospective")]
    [InlineData("reorder")]
    [InlineData("link")]
    [InlineData("duplicate")]
    public async Task Handler_CommandCountDoesNotGrowWithTheHabitSet(string operation)
    {
        var small = await Run(operation, 2);
        var large = await Run(operation, 50);
        large.Selects.Should().Be(small.Selects, operation);
        if (operation == "reorder") (large.Writes - small.Writes).Should().Be(48);
        else large.Writes.Should().Be(small.Writes, operation);
    }

    [Fact]
    public async Task DailySummary_UsesOneRealPipelineAndOneFullHabitReadAtOneThousandHabits()
    {
        var small = await Run("summary", 1);
        var large = await Run("summary", 1000);
        large.Selects.Should().Be(small.Selects);
        large.Writes.Should().Be(0);
    }

    private static async Task<(int Selects, int Writes)> Run(string operation, int count)
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var db = factory.Context;
        var user = User.Create("Read", "bounded-handler@example.test").Value;
        user.SetStripeSubscription("sub", DateTime.UtcNow.AddYears(1));
        user.SetTimeZone("UTC");
        db.Users.Add(user);
        var habits = Enumerable.Range(0, count).Select(index => Habit.Create(new HabitCreateParams(
            HabitSchedule.Recurring, user.Id, $"Habit {index}", operation == "reschedule" ? FrequencyUnit.Week : FrequencyUnit.Day, 1,
            Today.AddDays(-10), Description: "Private description")).Value).ToArray();
        foreach (var habit in habits.Where(_ => operation != "reschedule"))
            habit.Log(Today.AddDays(-2), note: "Private note", advanceDueDate: false).IsSuccess.Should().BeTrue();
        var goal = Goal.Create(user.Id, "Goal", 10, "times").Value;
        db.Habits.AddRange(habits);
        db.Goals.Add(goal);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var hr = new GenericRepository<Habit>(db);
        var lr = new GenericRepository<HabitLog>(db);
        var ur = new GenericRepository<User>(db);
        var unit = new UnitOfWork(db, new DatabaseConnectionSettings());
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(Today);
        dates.GetUserWeekStartDayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(1);
        dates.GetUserTimeZoneAsync(user.Id, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(user.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());
        var gate = Substitute.For<IPayGateService>();
        gate.CanUseDailySummary(user.Id, Arg.Any<CancellationToken>()).Returns(Result.Success());
        gate.CanUseRetrospective(user.Id, Arg.Any<CancellationToken>()).Returns(Result.Success());
        gate.CanUseSmartReschedule(user.Id, Arg.Any<CancellationToken>()).Returns(Result.Success());
        gate.CanCreateHabits(user.Id, 1, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var reminders = Substitute.For<IRepeatReminderReader>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var schedule = new GetHabitScheduleQueryHandler(hr, new HabitScheduleLogReader(db), new HabitSchedulePageLoader(db), dates, unit, reminders);
        counter.Reset();
        switch (operation)
        {
            case "schedule":
                var page = await schedule.Handle(new(user.Id, Today, Today, IncludeOverdue: true, PageSize: 1), default);
                Ensure(page);
                page.Value.Items.Should().ContainSingle();
                counter.Commands.Where(c => c.Sql.Contains("\"Description\"", StringComparison.Ordinal)).Should().OnlyContain(c => c.Rows <= 1);
                break;
            case "summary":
                var mediator = Substitute.For<IMediator>();
                mediator.Send(Arg.Any<GetDailySummaryScheduleQuery>(), Arg.Any<CancellationToken>())
                    .Returns(call => new GetDailySummaryScheduleQueryHandler(hr, new HabitScheduleLogReader(db), new HabitSchedulePageLoader(db), dates, unit, reminders)
                        .Handle(call.Arg<GetDailySummaryScheduleQuery>(), call.Arg<CancellationToken>()));
                Ensure(await new GetDailySummaryQueryHandler(hr, ur, lr, new HabitSummaryLogReader(db), gate,
                    Substitute.For<ISummaryService>(), cache, dates, mediator).Handle(new(user.Id, Today, Today, "en", false), default));
                await mediator.Received(1).Send(Arg.Any<GetDailySummaryScheduleQuery>(), Arg.Any<CancellationToken>());
                counter.Commands.Count(c => c.Sql.Split("FROM", 2)[0].Contains("\"h\".\"Description\"", StringComparison.Ordinal)).Should().Be(1);
                break;
            case "detail": Ensure(await new GetHabitByIdQueryHandler(hr, lr, dates, reminders).Handle(new(user.Id, habits[0].Id), default)); break;
            case "logs": Ensure(await new GetHabitLogsQueryHandler(hr, new HabitLogReader(db), dates).Handle(new(user.Id, habits[0].Id, Today.AddDays(-7), Today), default)); break;
            case "all-logs": Ensure(await new GetAllHabitLogsQueryHandler(hr, lr).Handle(new(user.Id, Today.AddDays(-7), Today), default)); break;
            case "metrics": Ensure(await new GetHabitMetricsQueryHandler(hr, ur, dates).Handle(new(user.Id, habits[0].Id), default)); break;
            case "period": Ensure(await new GetPeriodAnalyticsQueryHandler(hr, ur).Handle(new(user.Id, Today.AddDays(-7), Today), default)); break;
            case "calendar": Ensure(await new GetCalendarMonthQueryHandler(hr, dates, unit, reminders).Handle(new(user.Id, Today.AddDays(-7), Today), default)); break;
            case "widget": Ensure(await new GetHabitWidgetQueryHandler(hr, lr, ur, dates, unit).Handle(new(user.Id), default)); break;
            case "trends": Ensure(await new GetHabitsCompletionTrendsQueryHandler(ur, hr, flags).Handle(new(user.Id, Today.AddDays(-7), Today), default)); break;
            case "streak": Ensure(await new GetStreakHistoryQueryHandler(ur, hr, lr, new GenericRepository<StreakFreeze>(db), flags).Handle(new(user.Id, Today.AddDays(-7), Today), default)); break;
            case "retrospective":
                Ensure(await new GetRetrospectiveQueryHandler(hr, gate, Substitute.For<IRetrospectiveService>(), Substitute.For<IUserStreakService>(), dates, cache)
                .Handle(new(user.Id, Today.AddDays(-7), Today, "week", "en", false), default)); break;
            case "reschedule":
                var ai = Substitute.For<IRescheduleSuggestionService>();
                ai.GenerateAsync(Arg.Any<Habit>(), Today, "en", Arg.Any<CancellationToken>()).Returns(Result.Success(new RescheduleSuggestion(FrequencyUnit.Day, 1, Today.AddDays(1), null, [], "Later")));
                Ensure(await new GetRescheduleSuggestionQueryHandler(hr, lr, ur, gate, ai, cache, dates).Handle(new(user.Id, habits[0].Id, "en"), default));
                break;
            case "count": (await new GetHabitCountQueryHandler(hr).Handle(new(user.Id), default)).Count.Should().Be(count); break;
            case "reorder": (await new ReorderHabitsCommandHandler(hr, unit).Handle(new(user.Id, habits.Select((h, index) => new HabitPositionUpdate(h.Id, index)).ToArray()), default)).IsSuccess.Should().BeTrue(); break;
            case "link":
                (await new LinkGoalsToHabitCommandHandler(hr, new GenericRepository<Goal>(db), Substitute.For<IGoalCompletionService>(), dates)
                    .Handle(new(user.Id, habits[0].Id, [goal.Id]), default)).IsSuccess.Should().BeTrue();
                await unit.SaveChangesAsync();
                break;
            case "duplicate": Ensure(await new DuplicateHabitCommandHandler(hr, lr, gate, unit, dates, cache).Handle(new(user.Id, habits[0].Id), default)); break;
        }
        return (counter.SelectCommandCount, counter.WriteCommandCount);
    }

    private static void Ensure<T>(Result<T> result) => result.IsSuccess.Should().BeTrue(result.Error);
}
