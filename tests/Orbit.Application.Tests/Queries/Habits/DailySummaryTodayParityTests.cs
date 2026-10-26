using System.Linq.Expressions;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Habits;

public class DailySummaryTodayParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Summary_UsesHojeRowsAndCompletionState(bool nestedCompletion)
    {
        var day = new DateOnly(2026, 10, 25);
        var userId = Guid.NewGuid();
        var habits = new List<Habit>();
        Habit Add(string title, DateOnly due, FrequencyUnit? unit = FrequencyUnit.Day, Guid? parentId = null)
        {
            var habit = Habit.Create(new HabitCreateParams(
                unit is null ? HabitSchedule.OneTime : HabitSchedule.Recurring, userId, title, unit,
                unit is null ? null : 1, due, ParentHabitId: parentId,
                CreatedAtUtc: new DateTime(2026, 9, 26, 3, 0, 0, DateTimeKind.Utc))).Value;
            habits.Add(habit);
            return habit;
        }
        var overdue = Add("Morning walk", day.AddDays(-2), null);
        var task = Add("Submit a document", day, null);
        var water = Add("Drink water", day);
        var reading = Add("Read for 20 minutes", day);
        var routine = nestedCompletion ? Add("Morning routine", day) : null;
        var stretching = Add("Stretch for five minutes", day, parentId: routine?.Id);
        foreach (var habit in new[] { water, reading, stretching })
            habit.Log(day).IsSuccess.Should().BeTrue();
        var future = Add("Future task", day.AddDays(1), null);
        var logs = habits.SelectMany(h => h.Logs).ToList();
        var repo = Substitute.For<IGenericRepository<Habit>>();
        repo.ProjectAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<HabitScheduleCandidate>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<IQueryable<Habit>, IQueryable<HabitScheduleCandidate>>>(1)(
                habits.Where(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()).AsQueryable()).ToList());
        repo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()).ToList());
        repo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()).ToList());
        var reader = Substitute.For<IHabitScheduleLogReader>();
        reader.ReadDaysAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<CancellationToken>())
            .Returns(call => logs.Where(l => call.ArgAt<IReadOnlyCollection<Guid>>(0).Contains(l.HabitId)
                    && l.Date >= call.ArgAt<DateOnly>(1) && l.Date <= call.ArgAt<DateOnly>(2))
                .GroupBy(l => new { l.HabitId, l.Date }).Select(g => new HabitScheduleLogDay(
                    g.Key.HabitId, g.Key.Date, g.Count(l => l.Value > 0), g.Count(l => l.Value == 0), true)).ToList());
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(day);
        dates.GetUserWeekStartDayAsync(userId, Arg.Any<CancellationToken>()).Returns(1);
        var schedule = new GetHabitScheduleQueryHandler(repo, reader, Substitute.For<IHabitSchedulePageLoader>(),
            dates, Substitute.For<IUnitOfWork>());
        var hoje = await schedule.Handle(new(userId, day, day, IncludeOverdue: true), default);
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(User.Create("Person", "person@example.com").Value);
        var payGate = Substitute.For<IPayGateService>();
        payGate.CanUseDailySummary(userId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var summaryLogs = Substitute.For<IHabitSummaryLogReader>();
        summaryLogs.ReadAsync(Arg.Any<IReadOnlyCollection<HabitSummaryLogWindow>>(), Arg.Any<CancellationToken>())
            .Returns(logs.Select(l => new HabitSummaryLogFact(l.HabitId, l.Date, l.Value)).ToList());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetHabitScheduleQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => schedule.Handle(call.Arg<GetHabitScheduleQuery>(), call.Arg<CancellationToken>()));
        var handler = new GetDailySummaryQueryHandler(repo, users, Substitute.For<IGenericRepository<HabitLog>>(),
            summaryLogs, payGate, Substitute.For<ISummaryService>(), cache, dates, mediator);
        var summary = await handler.Handle(new(userId, day, day, "pt-BR", GenerateNarrative: false), default);

        hoje.IsSuccess.Should().BeTrue();
        summary.IsSuccess.Should().BeTrue();
        var card = summary.Value.ReadCard!;
        var expectedIds = hoje.Value.Items.SelectMany(h => new[] { h.Id }.Concat(h.Children.Select(c => c.Id)));
        card.Rows!.Select(r => Guid.Parse(r.Id)).Should().Equal(expectedIds);
        card.Rows.Should().NotContain(r => r.Id == future.Id.ToString());
        card.Rows.Should().Contain(r => r.Id == task.Id.ToString() && r.Status == "today");
        card.Rows.Should().Contain(r => r.Id == overdue.Id.ToString() && r.Status == "overdue");
        card.Rows.Count(r => r.Status == "done").Should().Be(3);
        card.Done.Should().Be(3);
        card.Due.Should().Be(nestedCompletion ? 6 : 5);
        card.OverdueCount.Should().Be(1);
        card.CompletionRate.Should().Be(nestedCompletion ? 50 : 60);
    }
}
