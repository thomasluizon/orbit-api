using FluentAssertions;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Habits;

public class GetAllHabitLogsQueryHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _logRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly GetAllHabitLogsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly DateFrom = new(2026, 4, 1);
    private static readonly DateOnly DateTo = new(2026, 4, 30);

    public GetAllHabitLogsQueryHandlerTests()
    {
        _handler = new GetAllHabitLogsQueryHandler(_habitRepo, _logRepo);
    }

    [Fact]
    public async Task Handle_WithHabitsAndLogs_ReturnsGroupedLogs()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test Habit", FrequencyUnit.Day, 1,
            DueDate: DateFrom)).Value;

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        _logRepo.FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<HabitLog>().AsReadOnly());

        var query = new GetAllHabitLogsQuery(UserId, DateFrom, DateTo);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_NoHabits_ReturnsEmptyDictionary()
    {
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        _logRepo.FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<HabitLog>().AsReadOnly());

        var query = new GetAllHabitLogsQuery(UserId, DateFrom, DateTo);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_NoLogs_ReturnsEmptyDictionary()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test Habit", FrequencyUnit.Day, 1,
            DueDate: DateFrom)).Value;

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        _logRepo.FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<HabitLog>().AsReadOnly());

        var query = new GetAllHabitLogsQuery(UserId, DateFrom, DateTo);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }
}
