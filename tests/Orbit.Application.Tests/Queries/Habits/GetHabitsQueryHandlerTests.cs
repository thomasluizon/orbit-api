using FluentAssertions;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Habits;

public class GetHabitsQueryHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly GetHabitsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public GetHabitsQueryHandlerTests()
    {
        _handler = new GetHabitsQueryHandler(_habitRepo);
    }

    private static Habit CreateTestHabit(string title = "Test Habit", FrequencyUnit? unit = FrequencyUnit.Day, int? qty = 1)
    {
        return Habit.Create(new HabitCreateParams(
            UserId, title, unit, qty,
            DueDate: Today)).Value;
    }

    [Fact]
    public async Task Handle_ReturnsHabitsForUser()
    {
        var habits = new List<Habit>
        {
            CreateTestHabit("Habit 1"),
            CreateTestHabit("Habit 2")
        };

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(habits.AsReadOnly());

        var query = new GetHabitsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.Should().HaveCount(2);
        result.Select(h => h.Title).Should().Contain("Habit 1").And.Contain("Habit 2");
    }

    [Fact]
    public async Task Handle_NoHabits_ReturnsEmptyList()
    {
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        var query = new GetHabitsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_SearchFilter_FiltersHabitsByTitle()
    {
        var habits = new List<Habit>
        {
            CreateTestHabit("Morning Run"),
            CreateTestHabit("Evening Yoga")
        };

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(habits.AsReadOnly());

        var query = new GetHabitsQuery(UserId, Search: "Morning");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Title.Should().Be("Morning Run");
    }

    [Fact]
    public async Task Handle_SearchFilter_CaseInsensitive()
    {
        var habits = new List<Habit>
        {
            CreateTestHabit("Morning Run")
        };

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(habits.AsReadOnly());

        var query = new GetHabitsQuery(UserId, Search: "morning");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task Handle_IsCompletedFilter_FiltersCorrectly()
    {
        var habit = CreateTestHabit("Daily Run");

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        var query = new GetHabitsQuery(UserId, IsCompleted: false);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_FrequencyUnitFilter_NoneReturnsOneTimeTasks()
    {
        var oneTime = CreateTestHabit("One-time Task", unit: null, qty: null);
        var daily = CreateTestHabit("Daily Habit");

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { oneTime, daily }.AsReadOnly());

        var query = new GetHabitsQuery(UserId, FrequencyUnitFilter: "none");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].FrequencyUnit.Should().BeNull();
    }

    [Fact]
    public async Task Handle_FrequencyUnitFilter_DayReturnsDailyHabits()
    {
        var oneTime = CreateTestHabit("One-time Task", unit: null, qty: null);
        var daily = CreateTestHabit("Daily Habit");

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { oneTime, daily }.AsReadOnly());

        var query = new GetHabitsQuery(UserId, FrequencyUnitFilter: "Day");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].FrequencyUnit.Should().Be(FrequencyUnit.Day);
    }

    [Fact]
    public async Task Handle_MapsResponseFieldsCorrectly()
    {
        var habit = CreateTestHabit("Mapped Habit");

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        var query = new GetHabitsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.Should().HaveCount(1);
        var response = result[0];
        response.Title.Should().Be("Mapped Habit");
        response.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        response.FrequencyQuantity.Should().Be(1);
        response.IsCompleted.Should().BeFalse();
        response.DueDate.Should().Be(Today);
        response.Children.Should().BeEmpty();
    }
}
