using System.Linq.Expressions;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Habits;

public sealed class GetHabitMonthQueryTests
{
    private readonly Guid _user = Guid.NewGuid();
    private readonly IUserDateService _dates = Substitute.For<IUserDateService>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IGenericRepository<Habit> _habits = Substitute.For<IGenericRepository<Habit>>();
    private readonly DateOnly _today = new(2026, 10, 26);

    [Fact]
    public async Task Handle_UsesActualCalendarProjectionIncludingChildrenAndSkipLogs()
    {
        _dates.GetUserTodayAsync(_user, Arg.Any<CancellationToken>()).Returns(_today);
        _dates.GetUserWeekStartDayAsync(_user, Arg.Any<CancellationToken>()).Returns(0);
        var start = new DateOnly(2026, 10, 1);
        var parent = Habit.Create(new(HabitSchedule.Recurring, _user, "Routine", FrequencyUnit.Day, 1, start)).Value;
        var child = Habit.Create(new(HabitSchedule.Flexible, _user, "Read", FrequencyUnit.Week, 3, start,
            ParentHabitId: parent.Id)).Value;
        parent.Log(start, advanceDueDate: false).IsSuccess.Should().BeTrue();
        child.SkipFlexible(start.AddDays(1)).IsSuccess.Should().BeTrue();
        _habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { parent, child }.AsReadOnly());
        var sourceHandler = new GetCalendarMonthQueryHandler(_habits, _dates, Substitute.For<IUnitOfWork>());
        CalendarMonthResponse? source = null;
        _mediator.Send(Arg.Any<GetCalendarMonthQuery>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var query = call.Arg<GetCalendarMonthQuery>();
            query.UserId.Should().Be(_user);
            query.DateFrom.Should().Be(start);
            query.DateTo.Should().Be(new DateOnly(2026, 10, 31));
            var result = await sourceHandler.Handle(query, default);
            source = result.Value;
            return result;
        });

        var result = await new GetHabitMonthQueryHandler(_mediator, _dates).Handle(new(_user, Language: "pt-BR"), default);

        result.IsSuccess.Should().BeTrue();
        var card = result.Value;
        card.Calendar.Should().BeSameAs(source);
        card.Today.Should().Be(_today);
        card.WeekStartDay.Should().Be(0);
        card.Calendar.Habits.Single().Children.Single().Id.Should().Be(child.Id);
        card.Calendar.Logs[parent.Id].Single().Value.Should().BeGreaterThan(0);
        card.Calendar.Logs[child.Id].Single().Value.Should().Be(0);
        card.Calendar.Habits.Single().ScheduledDates.Should().Contain(start.AddDays(2), "unlogged scheduled days must survive for missed-day rendering");
        card.DateTo.Should().Be(new DateOnly(2026, 10, 31), "future days remain part of the calendar grid");
        card.Language.Should().Be("pt-BR");
    }

    [Fact]
    public async Task Handle_InvalidRangeNeverDispatchesCalendarAndSourceFailurePropagates()
    {
        _dates.GetUserTodayAsync(_user, Arg.Any<CancellationToken>()).Returns(_today);
        var handler = new GetHabitMonthQueryHandler(_mediator, _dates);
        (await handler.Handle(new(_user, _today, _today.AddDays(-1)), default)).IsFailure.Should().BeTrue();
        _mediator.ReceivedCalls().Should().BeEmpty();
        _mediator.Send(Arg.Any<GetCalendarMonthQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<CalendarMonthResponse>("unavailable", "SOURCE_FAILED"));
        (await handler.Handle(new(_user), default)).ErrorCode.Should().Be("SOURCE_FAILED");
    }

    [Theory]
    [InlineData(null, null, "en", true)]
    [InlineData("2024-02-01", "2024-02-29", "pt-BR", true)]
    [InlineData("2026-10-01", "2026-09-30", "en", false)]
    [InlineData("2026-01-01", "2026-06-01", "en", false)]
    [InlineData(null, null, "es", false)]
    public void Validator_GuardsEffectiveRangeAndLanguage(string? from, string? to, string language, bool valid)
    {
        var query = new GetHabitMonthQuery(_user, from is null ? null : DateOnly.Parse(from),
            to is null ? null : DateOnly.Parse(to), language);
        new GetHabitMonthQueryValidator().Validate(query).IsValid.Should().Be(valid);
    }
}
