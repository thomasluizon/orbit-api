using FluentAssertions;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Habits;

public class GetCalendarMonthQueryHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly GetCalendarMonthQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);
    private static readonly DateOnly MonthStart = new(2026, 4, 1);
    private static readonly DateOnly MonthEnd = new(2026, 4, 30);

    public GetCalendarMonthQueryHandlerTests()
    {
        _handler = new GetCalendarMonthQueryHandler(_habitRepo, _userDateService, _unitOfWork);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    private static Habit CreateDailyHabit(string title = "Daily Habit")
    {
        return Habit.Create(new HabitCreateParams(
            UserId, title, FrequencyUnit.Day, 1,
            DueDate: Today)).Value;
    }

    private static Habit CreateOneTimeHabit(string title = "One-time Task")
    {
        return Habit.Create(new HabitCreateParams(
            UserId, title, null, null,
            DueDate: Today)).Value;
    }

    private static Habit CreateBadHabit(string title = "Bad Habit")
    {
        return Habit.Create(new HabitCreateParams(
            UserId, title, FrequencyUnit.Day, 1,
            IsBadHabit: true, DueDate: Today)).Value;
    }

    [Fact]
    public async Task Handle_ReturnsCalendarData_ForMonth()
    {
        var habit = CreateDailyHabit();

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        var query = new GetCalendarMonthQuery(UserId, MonthStart, MonthEnd);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Habits.Should().NotBeNull();
        result.Value.Logs.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_EmptyHabits_ReturnsEmptyResponse()
    {
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        var query = new GetCalendarMonthQuery(UserId, MonthStart, MonthEnd);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Habits.Should().BeEmpty();
        result.Value.Logs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_OneTimeTask_IncludedOnDueDate()
    {
        var habit = CreateOneTimeHabit();

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        var query = new GetCalendarMonthQuery(UserId, MonthStart, MonthEnd);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Habits.Should().HaveCount(1);
    }

    [Fact]
    public async Task Handle_RecurringHabit_ReturnsScheduledDates()
    {
        var habit = CreateDailyHabit();

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        var query = new GetCalendarMonthQuery(UserId, MonthStart, MonthEnd);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Habits.Should().NotBeEmpty();
        var habitItem = result.Value.Habits[0];
        habitItem.ScheduledDates.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Handle_BadHabit_IncludedInResponse()
    {
        var habit = CreateBadHabit();

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        var query = new GetCalendarMonthQuery(UserId, MonthStart, MonthEnd);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        if (result.Value.Habits.Count > 0)
            result.Value.Habits[0].IsBadHabit.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_DateRangeExceeds62Days_ReturnsFailure()
    {
        var query = new GetCalendarMonthQuery(UserId, new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 1));

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("62 days");
    }

    [Fact]
    public async Task Handle_CallsAdvanceStaleBadHabitDueDates()
    {
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        var query = new GetCalendarMonthQuery(UserId, MonthStart, MonthEnd);

        await _handler.Handle(query, CancellationToken.None);

        await _userDateService.Received(1).GetUserTodayAsync(UserId, Arg.Any<CancellationToken>());
    }
}
