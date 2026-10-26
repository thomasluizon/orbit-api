using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat;

public sealed class HabitMonthModelPayloadTests
{
    [Fact]
    public void EmptyCalendarRetainsTheWholeWindow()
    {
        var from = new DateOnly(2026, 10, 1);
        var to = new DateOnly(2026, 10, 31);
        var card = new HabitMonthCard(from, to, from.AddDays(25), 1, new([], []));

        var payload = HabitMonthModelPayload.From(card);

        payload.DateFrom.Should().Be(from);
        payload.DateTo.Should().Be(to);
        payload.Today.Should().Be(card.Today);
        payload.Habits.Should().BeEmpty();
    }

    [Fact]
    public async Task CountsCalendarDaysWithoutTreatingSkipsAsCompletionsOrFutureDatesAsMissed()
    {
        var user = Guid.NewGuid();
        var from = new DateOnly(2026, 10, 1);
        var to = new DateOnly(2026, 10, 31);
        var today = from.AddDays(25);
        var habit = Habit.Create(new(HabitSchedule.Flexible, user, "Read", FrequencyUnit.Month, 50, from)).Value;
        habit.Log(from.AddDays(-1), advanceDueDate: false).IsSuccess.Should().BeTrue();
        habit.Log(from, advanceDueDate: false).IsSuccess.Should().BeTrue();
        habit.Log(from, advanceDueDate: false).IsSuccess.Should().BeTrue();
        habit.Log(from.AddDays(27), advanceDueDate: false).IsSuccess.Should().BeTrue();
        habit.SkipFlexible(from).IsSuccess.Should().BeTrue();
        habit.SkipFlexible(from.AddDays(1)).IsSuccess.Should().BeTrue();
        habit.SkipFlexible(from.AddDays(1)).IsSuccess.Should().BeTrue();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user, Arg.Any<CancellationToken>()).Returns(today);
        var repository = Substitute.For<IGenericRepository<Habit>>();
        repository.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());
        var calendar = (await new GetCalendarMonthQueryHandler(repository, dates, Substitute.For<IUnitOfWork>())
            .Handle(new(user, from, to), default)).Value;

        var payload = HabitMonthModelPayload.From(new(from, to, today, 1, calendar));

        var row = payload.Habits.Should().ContainSingle().Subject;
        row.Id.Should().Be(habit.Id);
        row.Title.Should().Be(habit.Title);
        row.Scheduled.Should().Be(31);
        row.Completed.Should().Be(2);
        row.Skipped.Should().Be(1);
        row.Missed.Should().Be(23);
        row.IsFlexible.Should().BeTrue();
        calendar.Logs[habit.Id].Should().HaveCount(6);
    }
}
